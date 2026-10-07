using System.Runtime.InteropServices;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// One graphics adapter as reported by DXGI.
/// </summary>
public sealed record DxgiAdapterInfo(
    string Description,
    ulong DedicatedVideoMemoryBytes,
    ulong SharedSystemMemoryBytes,
    uint VendorId,
    bool IsSoftwareAdapter)
{
    /// <summary>
    /// A discrete card has its own video memory; integrated graphics borrow system RAM and
    /// report little or no dedicated memory.
    /// </summary>
    public bool IsDiscrete => !IsSoftwareAdapter && DedicatedVideoMemoryBytes > 512UL * 1024 * 1024;

    public string VendorName => VendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        _ => "Unknown"
    };
}

/// <summary>
/// Reads graphics adapters through DXGI.
/// </summary>
/// <remarks>
/// This exists because <c>Win32_VideoController.AdapterRAM</c> is a 32-bit value that saturates
/// at 4 GB, so every card above that size is reported as exactly 4.0 GB. The registry value
/// <c>HardwareInformation.qwMemorySize</c> is correct when present but is not always written and
/// requires matching the adapter by driver description. <c>DXGI_ADAPTER_DESC1.DedicatedVideoMemory</c>
/// is a native pointer-sized value straight from the driver and is what Task Manager reports.
/// </remarks>
public static class DxgiAdapterService
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr ppFactory);

    public static IReadOnlyList<DxgiAdapterInfo> GetAdapters()
    {
        List<DxgiAdapterInfo> adapters = new();
        IntPtr factoryPtr = IntPtr.Zero;

        try
        {
            if (CreateDXGIFactory1(in IID_IDXGIFactory1, out factoryPtr) != 0 || factoryPtr == IntPtr.Zero)
            {
                return adapters;
            }

            IDXGIFactory1 factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);

            for (uint index = 0; ; index++)
            {
                if (factory.EnumAdapters1(index, out IDXGIAdapter1? adapter) != 0 || adapter is null)
                {
                    break;
                }

                try
                {
                    if (adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc) == 0)
                    {
                        // DXGI_ADAPTER_FLAG_SOFTWARE is 2 - the WARP renderer, not real hardware.
                        bool software = (desc.Flags & 2u) != 0;
                        adapters.Add(new DxgiAdapterInfo(
                            desc.Description?.TrimEnd('\0') ?? "Graphics adapter",
                            (ulong)desc.DedicatedVideoMemory.ToUInt64(),
                            (ulong)desc.SharedSystemMemory.ToUInt64(),
                            desc.VendorId,
                            software));
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }

            Marshal.ReleaseComObject(factory);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Enumerate DXGI adapters", exception);
        }
        finally
        {
            if (factoryPtr != IntPtr.Zero)
            {
                Marshal.Release(factoryPtr);
            }
        }

        return adapters;
    }

    /// <summary>
    /// Returns dedicated video memory for the adapter whose description best matches
    /// <paramref name="adapterName"/>, or 0 when there is no usable match.
    /// </summary>
    public static ulong GetDedicatedMemoryBytes(string adapterName)
    {
        IReadOnlyList<DxgiAdapterInfo> adapters = GetAdapters();
        if (adapters.Count == 0)
        {
            return 0;
        }

        foreach (DxgiAdapterInfo adapter in adapters)
        {
            if (adapter.Description.Equals(adapterName, StringComparison.OrdinalIgnoreCase) ||
                adapter.Description.Contains(adapterName, StringComparison.OrdinalIgnoreCase) ||
                adapterName.Contains(adapter.Description, StringComparison.OrdinalIgnoreCase))
            {
                return adapter.DedicatedVideoMemoryBytes;
            }
        }

        // No name match: if exactly one hardware adapter exists, it is unambiguously the one.
        List<DxgiAdapterInfo> hardware = adapters.Where(item => !item.IsSoftwareAdapter).ToList();
        return hardware.Count == 1 ? hardware[0].DedicatedVideoMemoryBytes : 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();

        // IDXGIFactory
        [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);

        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1? adapter);
        [PreserveSig] bool IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIObject
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();

        // IDXGIAdapter
        [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
        [PreserveSig] int GetDesc(out IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(in Guid name, out long umdVersion);

        // IDXGIAdapter1
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
