using System.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// Saturates a chosen GPU with a Direct3D 11 compute workload.
/// </summary>
/// <remarks>
/// The WPF render load this replaces is driven by <c>CompositionTarget.Rendering</c>, which
/// presents once per monitor refresh. On a 100 Hz panel that is a 10 ms budget per frame, and a
/// few hundred low-poly spheres use only about a third of it - which is why an RTX 2080 Ti sat at
/// roughly 32% and 29 W no matter how much geometry was added or how large the window was made.
///
/// A compute dispatch has no swap chain and no present, so nothing throttles it to the refresh
/// rate. It also runs on an explicitly chosen adapter, which is what makes separate discrete and
/// integrated testing possible - WPF renders on whichever adapter Windows assigns it.
/// </remarks>
public sealed class GpuComputeLoadController : IDisposable
{
    // Each thread runs InnerIterations of dependent FMA and transcendental work. The chain is
    // dependent on purpose: independent maths would be reordered and pipelined, drawing less power.
    private const string ComputeShaderSource = @"
RWStructuredBuffer<float4> Output : register(u0);

cbuffer Params : register(b0)
{
    uint InnerIterations;
    uint Seed;
    uint Pad0;
    uint Pad1;
};

[numthreads(256, 1, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    float4 acc = float4(id.x + Seed, id.x * 0.5f, id.x * 0.25f, 1.0f) * 0.0001f;

    [loop]
    for (uint i = 0; i < InnerIterations; i++)
    {
        acc = mad(acc, 1.0000001f, float4(0.0000001f, 0.0000002f, 0.0000003f, 0.0000004f));
        acc = sin(acc) * cos(acc) + acc * 1.0001f;
        acc = rsqrt(abs(acc) + 0.0001f) * 0.0001f + acc;
    }

    Output[id.x] = acc;
}";

    private const int ThreadsPerGroup = 256;
    private const int ThreadGroups = 2048;
    private const uint InnerIterations = 2048;

    private readonly object _syncRoot = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _shader;
    private ID3D11Buffer? _outputBuffer;
    private ID3D11UnorderedAccessView? _outputView;
    private ID3D11Buffer? _parameterBuffer;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private bool _disposed;

    public string AdapterDescription { get; private set; } = string.Empty;

    public string LastError { get; private set; } = string.Empty;

    public bool IsRunning => _worker is { IsCompleted: false };

    /// <summary>
    /// Creates a device on the adapter whose description matches <paramref name="adapterDescription"/>.
    /// Pass null or empty to use the first hardware adapter.
    /// </summary>
    public bool TryInitialize(string? adapterDescription)
    {
        lock (_syncRoot)
        {
            ReleaseDeviceResources();

            try
            {
                using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                IDXGIAdapter1? selected = null;

                for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Success; index++)
                {
                    if (adapter is null)
                    {
                        break;
                    }

                    bool software = (adapter.Description1.Flags & AdapterFlags.Software) != 0;
                    bool matches = string.IsNullOrWhiteSpace(adapterDescription) ||
                        adapter.Description1.Description.Contains(adapterDescription, StringComparison.OrdinalIgnoreCase);

                    if (!software && matches && selected is null)
                    {
                        selected = adapter;
                    }
                    else
                    {
                        adapter.Dispose();
                    }
                }

                if (selected is null)
                {
                    LastError = "No matching hardware graphics adapter was found.";
                    return false;
                }

                AdapterDescription = selected.Description1.Description;

                // DriverType.Unknown is required when an explicit adapter is supplied.
                D3D11.D3D11CreateDevice(
                    selected,
                    DriverType.Unknown,
                    DeviceCreationFlags.None,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                    out ID3D11Device device,
                    out _,
                    out ID3D11DeviceContext context).CheckError();

                selected.Dispose();

                _device = device;
                _context = context;

                using Blob compiled = CompileShader();
                _shader = device.CreateComputeShader(compiled.AsSpan());

                BufferDescription outputDescription = new()
                {
                    ByteWidth = ThreadsPerGroup * ThreadGroups * 16,
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.UnorderedAccess,
                    StructureByteStride = 16,
                    MiscFlags = ResourceOptionFlags.BufferStructured
                };
                _outputBuffer = device.CreateBuffer(outputDescription);
                _outputView = device.CreateUnorderedAccessView(_outputBuffer);

                _parameterBuffer = device.CreateBuffer(
                    new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default));

                LastError = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                ApplicationLogService.WriteException("Initialize GPU compute load", exception);
                LastError = exception.Message;
                ReleaseDeviceResources();
                return false;
            }
        }
    }

    private static Blob CompileShader()
    {
        Compiler.Compile(
            ComputeShaderSource,
            entryPoint: "CSMain",
            sourceName: "GpuComputeLoad.hlsl",
            profile: "cs_5_0",
            out Blob compiled,
            out Blob? errors);

        errors?.Dispose();
        return compiled;
    }

    /// <summary>
    /// Starts dispatching. <paramref name="loadPercent"/> is applied as a duty cycle: the loop
    /// runs flat out for that share of each 100 ms window and idles for the rest, which is the
    /// only honest way to hit a partial load without changing what the shader does.
    /// </summary>
    public void Start(int loadPercent)
    {
        lock (_syncRoot)
        {
            if (_device is null || _shader is null || IsRunning)
            {
                return;
            }

            int duty = Math.Clamp(loadPercent, 5, 100);
            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;

            _worker = Task.Factory.StartNew(
                () => DispatchLoop(duty, token),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    private void DispatchLoop(int dutyPercent, CancellationToken token)
    {
        try
        {
            ID3D11DeviceContext context = _context!;
            context.CSSetShader(_shader);
            context.CSSetUnorderedAccessView(0, _outputView);

            Span<uint> parameters = stackalloc uint[4] { InnerIterations, 1u, 0u, 0u };
            context.UpdateSubresource(parameters, _parameterBuffer!);
            context.CSSetConstantBuffer(0, _parameterBuffer);

            Stopwatch window = Stopwatch.StartNew();
            const int WindowMilliseconds = 100;

            while (!token.IsCancellationRequested)
            {
                window.Restart();
                double busyTarget = WindowMilliseconds * dutyPercent / 100.0;

                while (window.Elapsed.TotalMilliseconds < busyTarget && !token.IsCancellationRequested)
                {
                    context.Dispatch(ThreadGroups, 1, 1);

                    // Flush forces the queued dispatches to the driver rather than letting them
                    // accumulate, which keeps the duty cycle honest.
                    context.Flush();
                }

                int idle = WindowMilliseconds - (int)window.Elapsed.TotalMilliseconds;
                if (idle > 0)
                {
                    token.WaitHandle.WaitOne(idle);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("GPU compute load dispatch", exception);
            LastError = exception.Message;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        Task? worker;

        lock (_syncRoot)
        {
            cancellation = _cancellation;
            worker = _worker;
            _cancellation = null;
            _worker = null;
        }

        try
        {
            cancellation?.Cancel();
            worker?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Stop GPU compute load", exception);
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private void ReleaseDeviceResources()
    {
        _outputView?.Dispose();
        _outputView = null;
        _outputBuffer?.Dispose();
        _outputBuffer = null;
        _parameterBuffer?.Dispose();
        _parameterBuffer = null;
        _shader?.Dispose();
        _shader = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        lock (_syncRoot)
        {
            ReleaseDeviceResources();
        }
    }
}
