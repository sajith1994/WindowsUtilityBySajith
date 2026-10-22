using System.Management;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.ServiceHost;

/// <summary>
/// Physical wired and Wi-Fi adapters with their addresses and "Wake on magic packet" setting, so the monitoring PC can
/// wake this one when it is asleep or off. Read at most every 10 minutes.
/// </summary>
internal sealed class AdapterWakeProvider
{
    private const uint MediaWifi = 9;
    private const uint MediaEthernet = 14;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly object _lock = new();
    private List<RemoteAdapterInfo> _adapters = new();
    private DateTime _readAt = DateTime.MinValue;

    public List<RemoteAdapterInfo> Current
    {
        get
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _readAt > Lifetime)
                {
                    _readAt = DateTime.UtcNow;
                    _adapters = Read();
                }

                return _adapters;
            }
        }
    }

    private static List<RemoteAdapterInfo> Read()
    {
        List<RemoteAdapterInfo> adapters = new();
        Dictionary<string, bool?> wake = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using ManagementObjectSearcher power = new(@"root\StandardCimv2", "SELECT Name, WakeOnMagicPacket FROM MSFT_NetAdapterPowerManagementSettingData");
            foreach (ManagementBaseObject setting in power.Get())
            {
                using (setting)
                {
                    // 0 unsupported, 1 disabled, 2 enabled.
                    wake[setting["Name"] as string ?? string.Empty] = Convert.ToUInt32(setting["WakeOnMagicPacket"] ?? 0u) switch
                    {
                        2 => true,
                        1 => false,
                        _ => null
                    };
                }
            }
        }
        catch (ManagementException exception)
        {
            // The adapters are still listed; whether they wake the PC is then unknown.
            ServiceLog.WriteException("Remote monitoring wake settings", exception);
        }

        try
        {
            // NdisPhysicalMedium is the real property (PowerShell shows it as PhysicalMediaType).
            using ManagementObjectSearcher searcher = new(@"root\StandardCimv2",
                "SELECT Name, PermanentAddress, NetworkAddresses, NdisPhysicalMedium, ConnectorPresent, MediaConnectState FROM MSFT_NetAdapter");
            foreach (ManagementBaseObject adapter in searcher.Get())
            {
                using (adapter)
                {
                    uint media = Convert.ToUInt32(adapter["NdisPhysicalMedium"] ?? 0u);
                    if (adapter["ConnectorPresent"] is not true || media is not (MediaEthernet or MediaWifi))
                    {
                        continue;
                    }

                    string mac = (adapter["NetworkAddresses"] as string[])?.FirstOrDefault(address => address.Length == 12)
                        ?? adapter["PermanentAddress"] as string ?? string.Empty;
                    if (FormatMac(mac) is not { } formatted)
                    {
                        continue;
                    }

                    string name = adapter["Name"] as string ?? string.Empty;
                    adapters.Add(new RemoteAdapterInfo
                    {
                        Name = name,
                        MacAddress = formatted,
                        IsWired = media == MediaEthernet,
                        IsConnected = Convert.ToUInt32(adapter["MediaConnectState"] ?? 0u) == 1,
                        WakeOnMagicPacket = wake.GetValueOrDefault(name)
                    });
                }
            }
        }
        catch (ManagementException exception)
        {
            ServiceLog.WriteException("Remote monitoring adapters", exception);
        }

        // Wired first: Wake-on-LAN is meant for wired adapters (Wi-Fi rarely wakes a PC that is off).
        return adapters.OrderByDescending(adapter => adapter.IsWired).ThenByDescending(adapter => adapter.IsConnected).ToList();
    }

    /// <summary>"DC045A06A5A6" as "DC-04-5A-06-A5-A6"; null for anything that is not a real 6-byte address.</summary>
    internal static string? FormatMac(string? raw)
    {
        string hex = new((raw ?? string.Empty).Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12 || hex.All(digit => digit == '0') || hex.All(digit => digit is 'F' or 'f'))
        {
            return null;
        }

        return string.Join("-", Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2).ToUpperInvariant()));
    }
}
