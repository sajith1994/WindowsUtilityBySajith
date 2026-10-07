using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// Measures real TCP throughput between two Windows Utility instances on the same LAN.
/// One PC runs the temporary server and the other runs the client test. Test traffic is
/// generated in memory; no local files or user data are transferred.
/// </summary>
public sealed class LanThroughputService : IAsyncDisposable
{
    public const int DefaultPort = 5201;
    private const string ProtocolPrefix = "WUT1";
    private const int BufferSize = 256 * 1024;
    private const int MaxHeaderBytes = 256;

    private TcpListener? _listener;
    private CancellationTokenSource? _serverCancellation;
    private Task? _serverTask;

    public bool IsServerRunning => _listener is not null;
    public int ServerPort { get; private set; } = DefaultPort;

    public event Action<string>? ServerActivity;

    public Task StartServerAsync(int port, CancellationToken cancellationToken = default)
    {
        if (IsServerRunning)
        {
            throw new InvalidOperationException("The LAN throughput server is already running.");
        }

        ValidatePort(port);
        cancellationToken.ThrowIfCancellationRequested();

        TcpListener listener = new(IPAddress.Any, port);
        listener.Start(backlog: 32);

        CancellationTokenSource serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = listener;
        _serverCancellation = serverCancellation;
        ServerPort = port;
        _serverTask = AcceptLoopAsync(listener, serverCancellation.Token);
        RaiseServerActivity($"LAN test server listening on TCP {port}.");
        return Task.CompletedTask;
    }

    public async Task StopServerAsync()
    {
        TcpListener? listener = _listener;
        CancellationTokenSource? cancellation = _serverCancellation;
        Task? serverTask = _serverTask;

        _listener = null;
        _serverCancellation = null;
        _serverTask = null;

        if (listener is null)
        {
            return;
        }

        try
        {
            cancellation?.Cancel();
            listener.Stop();
            if (serverTask is not null)
            {
                await serverTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellation?.Dispose();
            RaiseServerActivity("LAN test server stopped.");
        }
    }

    public async Task<LanThroughputResult> RunClientTestAsync(
        string host,
        int port,
        int durationSeconds,
        int parallelStreams,
        IProgress<LanSpeedTestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Enter the IP address or hostname of the other PC running the LAN test server.", nameof(host));
        }

        ValidatePort(port);
        if (durationSeconds is < 3 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Test duration must be between 3 and 60 seconds.");
        }

        if (parallelStreams is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(parallelStreams), "Parallel streams must be between 1 and 8.");
        }

        progress?.Report(new LanSpeedTestProgress
        {
            Phase = "Latency",
            Detail = $"Checking reachability to {host}..."
        });
        double? latencyMs = await MeasureLatencyAsync(host, port, cancellationToken).ConfigureAwait(false);

        progress?.Report(new LanSpeedTestProgress
        {
            Phase = "Upload",
            Detail = $"Sending generated test traffic to {host}:{port} using {parallelStreams} TCP stream(s) for {durationSeconds} seconds...",
            LatencyMs = latencyMs
        });
        double uploadMbps = await RunAggregateDirectionAsync(
            host,
            port,
            "UPLOAD",
            durationSeconds,
            parallelStreams,
            sample => progress?.Report(new LanSpeedTestProgress
            {
                Phase = "Upload",
                Detail = $"Sending generated test traffic to {host}:{port} - {sample.AverageMbps:0.#} Mbps average...",
                LatencyMs = latencyMs,
                UploadMbps = sample.AverageMbps,
                CurrentMbps = sample.CurrentMbps,
                ProgressPercent = sample.ProgressPercent
            }),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new LanSpeedTestProgress
        {
            Phase = "Download",
            Detail = $"Receiving generated test traffic from {host}:{port} using {parallelStreams} TCP stream(s) for {durationSeconds} seconds...",
            LatencyMs = latencyMs,
            UploadMbps = uploadMbps
        });
        double downloadMbps = await RunAggregateDirectionAsync(
            host,
            port,
            "DOWNLOAD",
            durationSeconds,
            parallelStreams,
            sample => progress?.Report(new LanSpeedTestProgress
            {
                Phase = "Download",
                Detail = $"Receiving generated test traffic from {host}:{port} - {sample.AverageMbps:0.#} Mbps average...",
                LatencyMs = latencyMs,
                UploadMbps = uploadMbps,
                DownloadMbps = sample.AverageMbps,
                CurrentMbps = sample.CurrentMbps,
                ProgressPercent = sample.ProgressPercent
            }),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new LanSpeedTestProgress
        {
            Phase = "Complete",
            Detail = "LAN throughput test completed. Results are the measured end-to-end TCP throughput between the two PCs.",
            LatencyMs = latencyMs,
            UploadMbps = uploadMbps,
            DownloadMbps = downloadMbps
        });

        return new LanThroughputResult
        {
            ServerHost = host.Trim(),
            Port = port,
            DurationSeconds = durationSeconds,
            ParallelStreams = parallelStreams,
            LatencyMs = latencyMs,
            UploadMbps = uploadMbps,
            DownloadMbps = downloadMbps,
            CompletedAt = DateTime.Now
        };
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("LAN throughput server accept loop", exception);
            RaiseServerActivity($"LAN server error: {exception.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            try
            {
                NetworkStream stream = client.GetStream();
                string header = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
                string[] parts = header.Split('|', StringSplitOptions.TrimEntries);
                if (parts.Length != 3 || !string.Equals(parts[0], ProtocolPrefix, StringComparison.Ordinal))
                {
                    await WriteLineAsync(stream, "ERROR|Unsupported protocol", cancellationToken).ConfigureAwait(false);
                    return;
                }

                string direction = parts[1].ToUpperInvariant();
                if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int durationSeconds) ||
                    durationSeconds is < 3 or > 60)
                {
                    await WriteLineAsync(stream, "ERROR|Invalid duration", cancellationToken).ConfigureAwait(false);
                    return;
                }

                string remote = client.Client.RemoteEndPoint?.ToString() ?? "remote client";
                RaiseServerActivity($"{direction.ToLowerInvariant()} test connected from {remote}.");

                if (direction == "UPLOAD")
                {
                    await HandleUploadReceiverAsync(client, stream, cancellationToken).ConfigureAwait(false);
                }
                else if (direction == "DOWNLOAD")
                {
                    await HandleDownloadSenderAsync(client, stream, durationSeconds, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await WriteLineAsync(stream, "ERROR|Unknown direction", cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException exception)
            {
                ApplicationLogService.WriteException("LAN throughput server connection", exception);
            }
            catch (SocketException exception)
            {
                ApplicationLogService.WriteException("LAN throughput server socket", exception);
            }
            catch (Exception exception)
            {
                ApplicationLogService.WriteException("LAN throughput server client", exception);
            }
        }
    }

    private static async Task HandleUploadReceiverAsync(
        TcpClient client,
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        await WriteLineAsync(stream, "READY", cancellationToken).ConfigureAwait(false);

        byte[] buffer = new byte[BufferSize];
        long totalBytes = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            totalBytes += read;
        }
        stopwatch.Stop();

        string result = "RESULT|" +
            totalBytes.ToString(CultureInfo.InvariantCulture) + "|" +
            Math.Max(stopwatch.Elapsed.TotalMilliseconds, 1).ToString("0.###", CultureInfo.InvariantCulture);
        await WriteLineAsync(stream, result, cancellationToken).ConfigureAwait(false);
    }

    private static async Task HandleDownloadSenderAsync(
        TcpClient client,
        NetworkStream stream,
        int durationSeconds,
        CancellationToken cancellationToken)
    {
        await WriteLineAsync(stream, "READY", cancellationToken).ConfigureAwait(false);
        byte[] buffer = CreateTestBuffer();
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        client.Client.Shutdown(SocketShutdown.Send);
    }

    private static async Task<double> RunAggregateDirectionAsync(
        string host,
        int port,
        string direction,
        int durationSeconds,
        int parallelStreams,
        Action<LanTransferSample>? sampleProgress,
        CancellationToken cancellationToken)
    {
        TransferCounter counter = new();
        Stopwatch wallClock = Stopwatch.StartNew();
        Task<long>[] streams = Enumerable.Range(0, parallelStreams)
            .Select(_ => RunSingleStreamAsync(host, port, direction, durationSeconds, counter, cancellationToken))
            .ToArray();
        Task<long[]> allStreams = Task.WhenAll(streams);
        long previousBytes = 0;
        double previousSeconds = 0;

        try
        {
            while (!allStreams.IsCompleted)
            {
                Task delay = Task.Delay(250, cancellationToken);
                Task completed = await Task.WhenAny(allStreams, delay).ConfigureAwait(false);
                if (completed == allStreams)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                long currentBytes = counter.TotalBytes;
                double currentSeconds = Math.Max(wallClock.Elapsed.TotalSeconds, 0.001);
                double sampleSeconds = Math.Max(currentSeconds - previousSeconds, 0.001);
                double currentMbps = Math.Max(0, currentBytes - previousBytes) * 8.0 / sampleSeconds / 1_000_000.0;
                double averageMbps = currentBytes * 8.0 / currentSeconds / 1_000_000.0;
                double progressPercent = Math.Clamp(currentSeconds / durationSeconds * 100.0, 0, 99);

                sampleProgress?.Invoke(new LanTransferSample(currentMbps, averageMbps, progressPercent));
                previousBytes = currentBytes;
                previousSeconds = currentSeconds;
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                await allStreams.ConfigureAwait(false);
            }
            catch
            {
                // Observe the cancelled stream tasks before preserving the caller's cancellation.
            }

            throw;
        }

        long[] transferred = await allStreams.ConfigureAwait(false);
        wallClock.Stop();

        long totalBytes = transferred.Sum();
        double seconds = Math.Max(wallClock.Elapsed.TotalSeconds, 0.001);
        double finalMbps = totalBytes * 8.0 / seconds / 1_000_000.0;
        sampleProgress?.Invoke(new LanTransferSample(finalMbps, finalMbps, 100));
        return finalMbps;
    }

    private static async Task<long> RunSingleStreamAsync(
        string host,
        int port,
        string direction,
        int durationSeconds,
        TransferCounter counter,
        CancellationToken cancellationToken)
    {
        using TcpClient client = new();
        client.NoDelay = true;
        await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        NetworkStream stream = client.GetStream();

        await WriteLineAsync(stream, $"{ProtocolPrefix}|{direction}|{durationSeconds}", cancellationToken).ConfigureAwait(false);
        string ready = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(ready, "READY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"LAN test server did not accept the request: {ready}");
        }

        return direction == "UPLOAD"
            ? await RunUploadStreamAsync(client, stream, durationSeconds, counter, cancellationToken).ConfigureAwait(false)
            : await RunDownloadStreamAsync(stream, counter, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> RunUploadStreamAsync(
        TcpClient client,
        NetworkStream stream,
        int durationSeconds,
        TransferCounter counter,
        CancellationToken cancellationToken)
    {
        byte[] buffer = CreateTestBuffer();
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(durationSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            counter.Add(buffer.Length);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        client.Client.Shutdown(SocketShutdown.Send);

        string result = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
        string[] parts = result.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !string.Equals(parts[0], "RESULT", StringComparison.Ordinal) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long bytes))
        {
            throw new InvalidOperationException($"LAN test server returned an invalid upload result: {result}");
        }

        return bytes;
    }

    private static async Task<long> RunDownloadStreamAsync(
        NetworkStream stream,
        TransferCounter counter,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BufferSize];
        long totalBytes = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            totalBytes += read;
            counter.Add(read);
        }

        return totalBytes;
    }

    private static async Task<double?> MeasureLatencyAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using Ping ping = new();
            List<long> samples = new();
            for (int index = 0; index < 4; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PingReply reply = await ping.SendPingAsync(host, 1000).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    samples.Add(reply.RoundtripTime);
                }
            }

            if (samples.Count > 0)
            {
                return samples.Average();
            }
        }
        catch
        {
            // ICMP may be blocked. Fall back to TCP connect time below.
        }

        try
        {
            using TcpClient client = new();
            Stopwatch stopwatch = Stopwatch.StartNew();
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] CreateTestBuffer()
    {
        byte[] buffer = new byte[BufferSize];
        for (int index = 0; index < buffer.Length; index++)
        {
            buffer[index] = (byte)(index % 251);
        }
        return buffer;
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] oneByte = new byte[1];
        using MemoryStream buffer = new();
        while (buffer.Length < MaxHeaderBytes)
        {
            int read = await stream.ReadAsync(oneByte, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (oneByte[0] == (byte)'\n')
            {
                break;
            }

            if (oneByte[0] != (byte)'\r')
            {
                buffer.WriteByte(oneByte[0]);
            }
        }

        if (buffer.Length >= MaxHeaderBytes)
        {
            throw new InvalidDataException("LAN test protocol header is too large.");
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task WriteLineAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "TCP port must be between 1024 and 65535.");
        }
    }

    private void RaiseServerActivity(string message)
    {
        ServerActivity?.Invoke(message);
    }

    private sealed record LanTransferSample(
        double CurrentMbps,
        double AverageMbps,
        double ProgressPercent);

    private sealed class TransferCounter
    {
        private long _totalBytes;

        public long TotalBytes => Interlocked.Read(ref _totalBytes);

        public void Add(int bytes)
        {
            Interlocked.Add(ref _totalBytes, bytes);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopServerAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
