using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// Live disk and network activity, as Task Manager shows it: each physical disk's active time and read/write rates
/// (Windows performance counters "PhysicalDisk", the same source Task Manager uses) and each connected adapter's
/// receive/send rates (from its byte counters). Call <see cref="Sample"/> every few seconds; each call reports the
/// activity since the previous one, so the first call returns empty lists.
/// </summary>
public sealed class ActivityMonitor : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;

    private readonly object _lock = new();
    private IntPtr _query;
    private IntPtr _idleCounter;
    private IntPtr _readCounter;
    private IntPtr _writeCounter;
    private bool _diskCountersReady;
    private bool _diskPrimed;
    private readonly Dictionary<string, (long Received, long Sent, DateTime At)> _lastNetwork = new(StringComparer.OrdinalIgnoreCase);

    public ActivityMonitor()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0
                && PdhAddEnglishCounter(_query, @"\PhysicalDisk(*)\% Idle Time", IntPtr.Zero, out _idleCounter) == 0
                && PdhAddEnglishCounter(_query, @"\PhysicalDisk(*)\Disk Read Bytes/sec", IntPtr.Zero, out _readCounter) == 0
                && PdhAddEnglishCounter(_query, @"\PhysicalDisk(*)\Disk Write Bytes/sec", IntPtr.Zero, out _writeCounter) == 0)
            {
                _diskCountersReady = true;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // No performance counters: network activity is still reported.
        }
    }

    public ActivitySample Sample()
    {
        lock (_lock)
        {
            return new ActivitySample(SampleDisks(), SampleNetwork());
        }
    }

    private List<DiskActivityInfo> SampleDisks()
    {
        List<DiskActivityInfo> disks = new();
        if (!_diskCountersReady || PdhCollectQueryData(_query) != 0)
        {
            return disks;
        }

        // Rates need two collections; the first one only primes the counters.
        if (!_diskPrimed)
        {
            _diskPrimed = true;
            return disks;
        }

        Dictionary<string, double> idle = ReadArray(_idleCounter);
        Dictionary<string, double> read = ReadArray(_readCounter);
        Dictionary<string, double> write = ReadArray(_writeCounter);
        foreach ((string instance, double idlePercent) in idle)
        {
            if (instance == "_Total")
            {
                continue;
            }

            // Instances look like "0 C:" or "1 D: E:" (disk number, then its drive letters).
            string[] parts = instance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string letters = string.Join(" ", parts.Skip(1));
            disks.Add(new DiskActivityInfo
            {
                Name = letters.Length > 0 ? $"Disk {parts[0]} ({letters})" : $"Disk {parts[0]}",
                Letters = letters,
                ActivePercent = Math.Clamp(100 - idlePercent, 0, 100),
                ReadBytesPerSec = read.GetValueOrDefault(instance),
                WriteBytesPerSec = write.GetValueOrDefault(instance)
            });
        }

        return disks.OrderBy(disk => disk.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<NetworkActivityInfo> SampleNetwork()
    {
        List<NetworkActivityInfo> adapters = new();
        DateTime now = DateTime.UtcNow;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 or NetworkInterfaceType.GigabitEthernet)
                || SystemResourceMonitorService.IsVirtualAdapter(nic))
            {
                continue;
            }

            IPInterfaceStatistics statistics;
            try
            {
                statistics = nic.GetIPStatistics();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            seen.Add(nic.Id);
            if (_lastNetwork.TryGetValue(nic.Id, out (long Received, long Sent, DateTime At) previous))
            {
                double seconds = Math.Max(0.001, (now - previous.At).TotalSeconds);
                adapters.Add(new NetworkActivityInfo
                {
                    Name = nic.Name,
                    Kind = nic.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile broadband",
                        _ => "Ethernet"
                    },
                    ReceiveBitsPerSec = Math.Max(0, statistics.BytesReceived - previous.Received) * 8 / seconds,
                    SendBitsPerSec = Math.Max(0, statistics.BytesSent - previous.Sent) * 8 / seconds,
                    LinkBitsPerSec = nic.Speed > 0 ? nic.Speed : 0
                });
            }

            _lastNetwork[nic.Id] = (statistics.BytesReceived, statistics.BytesSent, now);
        }

        foreach (string gone in _lastNetwork.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _lastNetwork.Remove(gone);
        }

        return adapters.OrderBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, double> ReadArray(IntPtr counter)
    {
        Dictionary<string, double> values = new(StringComparer.OrdinalIgnoreCase);
        uint size = 0;
        uint status = PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out uint count, IntPtr.Zero);
        if (status != PdhMoreData || size == 0)
        {
            return values;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out count, buffer) != 0)
            {
                return values;
            }

            // PDH_FMT_COUNTERVALUE_ITEM: name pointer, then { DWORD status; (padding); double value }.
            int itemSize = IntPtr.Size + 16;
            for (int index = 0; index < count; index++)
            {
                IntPtr item = buffer + index * itemSize;
                string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                int itemStatus = Marshal.ReadInt32(item + IntPtr.Size);
                if (name is not null && itemStatus is 0 or 1)
                {
                    values[name] = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return values;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                _diskCountersReady = false;
            }
        }
    }

    public static string FormatBytesPerSecond(double bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / 1024 / 1024 / 1024:0.0} GB/s",
        >= 1024 * 1024 => $"{bytes / 1024 / 1024:0.0} MB/s",
        >= 1024 => $"{bytes / 1024:0} KB/s",
        _ => $"{bytes:0} B/s"
    };

    public static string FormatBitsPerSecond(double bits) => bits switch
    {
        >= 1_000_000_000 => $"{bits / 1_000_000_000:0.0} Gbps",
        >= 1_000_000 => $"{bits / 1_000_000:0.0} Mbps",
        _ => $"{bits / 1_000:0} Kbps"
    };

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

public sealed record ActivitySample(List<DiskActivityInfo> Disks, List<NetworkActivityInfo> Adapters);
