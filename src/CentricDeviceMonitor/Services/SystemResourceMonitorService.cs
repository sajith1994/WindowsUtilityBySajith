using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;

namespace CentricDeviceMonitor.Services;

public sealed record NetworkAdapterState(
    string Id,
    string Name,
    string Description,
    string Type,
    bool IsConnected,
    string IpAddresses);

public sealed record SystemResourceReading(
    double? CpuUsagePercent,
    double RamUsagePercent,
    ulong TotalPhysicalMemoryBytes,
    ulong AvailablePhysicalMemoryBytes,
    bool NetworkConnected,
    string NetworkSummary,
    IReadOnlyList<NetworkAdapterState> NetworkAdapters,
    string CpuModel,
    string RamSpeedDisplay);

public sealed class SystemResourceMonitorService
{
    private ulong? _previousIdle;
    private ulong? _previousKernel;
    private ulong? _previousUser;
    private readonly string _cpuModel;
    private readonly string _ramSpeedDisplay;

    public SystemResourceMonitorService()
    {
        _cpuModel = ReadCpuModel();
        _ramSpeedDisplay = ReadRamSpeedDisplay();
    }

    public SystemResourceReading Read()
    {
        double? cpuUsage = ReadCpuUsagePercent();
        (double ramUsage, ulong totalMemory, ulong availableMemory) = ReadMemoryUsage();
        IReadOnlyList<NetworkAdapterState> adapters = ReadNetworkAdapters();
        List<NetworkAdapterState> connectedAdapters = adapters.Where(adapter => adapter.IsConnected).ToList();
        string networkSummary = connectedAdapters.Count == 0
            ? "Disconnected"
            : string.Join(", ", connectedAdapters.Select(adapter =>
                string.IsNullOrWhiteSpace(adapter.IpAddresses)
                    ? $"{adapter.Type}: {adapter.Name}"
                    : $"{adapter.Type}: {adapter.Name} ({adapter.IpAddresses})"));

        return new SystemResourceReading(
            cpuUsage,
            ramUsage,
            totalMemory,
            availableMemory,
            connectedAdapters.Count > 0,
            networkSummary,
            adapters,
            _cpuModel,
            _ramSpeedDisplay);
    }

    private double? ReadCpuUsagePercent()
    {
        if (!GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime))
        {
            return null;
        }

        ulong idle = ToUInt64(idleTime);
        ulong kernel = ToUInt64(kernelTime);
        ulong user = ToUInt64(userTime);

        if (!_previousIdle.HasValue || !_previousKernel.HasValue || !_previousUser.HasValue)
        {
            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;
            return null;
        }

        ulong idleDelta = idle - _previousIdle.Value;
        ulong kernelDelta = kernel - _previousKernel.Value;
        ulong userDelta = user - _previousUser.Value;
        ulong totalDelta = kernelDelta + userDelta;

        _previousIdle = idle;
        _previousKernel = kernel;
        _previousUser = user;

        if (totalDelta == 0)
        {
            return null;
        }

        double busy = 100.0 * (totalDelta - Math.Min(idleDelta, totalDelta)) / totalDelta;
        return Math.Clamp(busy, 0.0, 100.0);
    }

    private static (double UsagePercent, ulong TotalBytes, ulong AvailableBytes) ReadMemoryUsage()
    {
        MemoryStatusEx status = new();
        status.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        if (!GlobalMemoryStatusEx(ref status))
        {
            return (0, 0, 0);
        }

        return (status.dwMemoryLoad, status.ullTotalPhys, status.ullAvailPhys);
    }

    private static IReadOnlyList<NetworkAdapterState> ReadNetworkAdapters()
    {
        List<NetworkAdapterState> adapters = new();
        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
            {
                continue;
            }

            // Windows reports filter and virtual adapters (WFP LightWeight Filter, Hyper-V, VPN
            // and VM bridges) as Ethernet and leaves them permanently Up even when every physical
            // link is down. Counting those made the app claim it was connected with no IP address.
            if (IsVirtualAdapter(networkInterface))
            {
                continue;
            }

            string type = networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                ? "Wi-Fi"
                : "Ethernet";

            string addresses = string.Empty;
            bool connected = false;

            if (networkInterface.OperationalStatus == OperationalStatus.Up)
            {
                try
                {
                    List<string> usable = networkInterface
                        .GetIPProperties()
                        .UnicastAddresses
                        .Select(address => address.Address)
                        .Where(address => address.AddressFamily == AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(address)
                            && !IsAutoConfiguration(address))
                        .Select(address => address.ToString())
                        .ToList();

                    // "Up" alone is not connectivity. An adapter with no routable IPv4 address, or
                    // only an APIPA 169.254.x.x self-assigned one, has no working network.
                    connected = usable.Count > 0;
                    addresses = string.Join(", ", usable);
                }
                catch
                {
                }
            }

            adapters.Add(new NetworkAdapterState(
                networkInterface.Id,
                networkInterface.Name,
                networkInterface.Description,
                type,
                connected,
                addresses));
        }

        return adapters;
    }

    /// <summary>APIPA addresses are self-assigned when DHCP fails and carry no connectivity.</summary>
    private static bool IsAutoConfiguration(IPAddress address)
    {
        byte[] octets = address.GetAddressBytes();
        return octets.Length == 4 && octets[0] == 169 && octets[1] == 254;
    }

    private static bool IsVirtualAdapter(NetworkInterface networkInterface)
    {
        string haystack = $"{networkInterface.Name} {networkInterface.Description}";
        foreach (string marker in VirtualAdapterMarkers)
        {
            if (haystack.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] VirtualAdapterMarkers =
    {
        "WFP", "LightWeight Filter", "QoS Packet Scheduler", "Native MAC Layer",
        "Virtual", "Hyper-V", "VMware", "VirtualBox", "Loopback", "Pseudo",
        "TAP-", "TAP Adapter", "Wintun", "WireGuard", "OpenVPN", "Tailscale", "ZeroTier",
        "WAN Miniport", "Microsoft Kernel Debug", "Bluetooth Device (Personal Area Network)",
        "Local Area Connection*"
    };

    private static string ReadCpuModel()
    {
        try
        {
            using ManagementObjectSearcher searcher = new("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                string? name = item["Name"]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }
        catch
        {
        }

        return "CPU model unavailable";
    }

    private static string ReadRamSpeedDisplay()
    {
        try
        {
            using ManagementObjectSearcher searcher = new("SELECT Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory");
            List<uint> speeds = new();
            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                uint speed = ConvertToUInt32(item["ConfiguredClockSpeed"]);
                if (speed == 0)
                {
                    speed = ConvertToUInt32(item["Speed"]);
                }

                if (speed > 0)
                {
                    speeds.Add(speed);
                }
            }

            if (speeds.Count == 0)
            {
                return "RAM speed unavailable";
            }

            uint[] distinct = speeds.Distinct().OrderBy(value => value).ToArray();
            return distinct.Length == 1
                ? $"{distinct[0]} MT/s"
                : $"{string.Join(" / ", distinct)} MT/s";
        }
        catch
        {
            return "RAM speed unavailable";
        }
    }

    private static uint ConvertToUInt32(object? value)
    {
        try
        {
            return value is null ? 0 : Convert.ToUInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static ulong ToUInt64(FileTime value) =>
        ((ulong)value.dwHighDateTime << 32) | value.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime lpIdleTime,
        out FileTime lpKernelTime,
        out FileTime lpUserTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
