using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// Reads the active Ethernet/Wi-Fi adapter, negotiated link rate, Wi-Fi radio details,
/// local traffic and gateway latency without requiring internet access. Internet speed
/// testing is launched through the official FAST.com website rather than an undocumented API.
/// </summary>
public sealed class NetworkSpeedTestService
{
    public async Task<NetworkLinkSnapshot> GetActiveLinkAsync(
        bool checkInternet = true,
        bool measureGatewayLatency = true,
        bool measureCurrentTraffic = true,
        CancellationToken cancellationToken = default)
    {
        NetworkInterface? adapter = SelectPreferredAdapter();
        if (adapter is null)
        {
            return new NetworkLinkSnapshot
            {
                InternetAvailable = false,
                InternetStatus = "No active Ethernet or Wi-Fi adapter"
            };
        }

        IPInterfaceProperties properties = adapter.GetIPProperties();
        string[] ipv4Addresses = properties.UnicastAddresses
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => address.Address.ToString())
            .ToArray();
        string localIp = ipv4Addresses.FirstOrDefault(address => !address.StartsWith("169.254.", StringComparison.Ordinal))
            ?? ipv4Addresses.FirstOrDefault()
            ?? "Not available";

        string gateway = properties.GatewayAddresses
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(item => item.Address.ToString())
            .FirstOrDefault(address => address != "0.0.0.0") ?? "Not available";

        string type = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
            ? "Wi-Fi"
            : adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                ? "Ethernet"
                : adapter.NetworkInterfaceType.ToString();

        WifiLinkDetails wifi = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
            ? await ReadWifiLinkDetailsAsync(adapter.Name, cancellationToken)
            : WifiLinkDetails.Empty;

        double? gatewayLatency = null;
        if (measureGatewayLatency && gateway != "Not available")
        {
            gatewayLatency = await MeasureGatewayLatencyAsync(gateway, cancellationToken);
        }

        (double? currentReceiveMbps, double? currentTransmitMbps) = measureCurrentTraffic
            ? await MeasureCurrentTrafficAsync(adapter, cancellationToken)
            : (null, null);

        bool internetAvailable = false;
        string internetStatus = "Not checked - open FAST.com to run the public internet test";
        if (checkInternet)
        {
            // Do not contact a third-party speed-test service during a local-link refresh.
            // This only confirms that Windows has an active network path/default gateway;
            // FAST.com is contacted only after the user explicitly opens it.
            // GetIsNetworkAvailable returns true for virtual and filter adapters that are permanently
            // "Up", so it reported a live network with every physical link down. Require a gateway
            // and a real adapter address instead.
            internetAvailable = gateway != "Not available" && !string.IsNullOrWhiteSpace(localIp) && localIp != "Not available";
            internetStatus = internetAvailable
                ? "Network path available - open FAST.com to measure internet speed"
                : "No default internet path detected - two-PC LAN testing is still available";
        }

        double? negotiatedMbps = adapter.Speed > 0 ? adapter.Speed / 1_000_000.0 : null;
        return new NetworkLinkSnapshot
        {
            AdapterName = adapter.Name,
            AdapterDescription = adapter.Description,
            InterfaceType = type,
            LocalIpAddress = localIp,
            GatewayAddress = gateway,
            NegotiatedLinkMbps = negotiatedMbps,
            WifiReceiveMbps = wifi.ReceiveMbps,
            WifiTransmitMbps = wifi.TransmitMbps,
            WifiSignalPercent = wifi.SignalPercent,
            WifiRadioType = wifi.RadioType,
            WifiChannel = wifi.Channel,
            GatewayLatencyMs = gatewayLatency,
            CurrentReceiveMbps = currentReceiveMbps,
            CurrentTransmitMbps = currentTransmitMbps,
            InternetAvailable = internetAvailable,
            InternetStatus = internetStatus
        };
    }

    private static NetworkInterface? SelectPreferredAdapter()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                          nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                          nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .Select(nic => new
            {
                Nic = nic,
                Properties = TryGetProperties(nic)
            })
            .Where(item => item.Properties is not null && HasAnyIpv4(item.Properties))
            .OrderBy(item => AdapterPriority(item.Nic, item.Properties!))
            .ThenByDescending(item => item.Nic.Speed)
            .Select(item => item.Nic)
            .FirstOrDefault();
    }

    private static int AdapterPriority(NetworkInterface nic, IPInterfaceProperties properties)
    {
        int priority = HasIpv4DefaultGateway(properties) ? 0 : 20;
        if (IsLikelyVirtualAdapter(nic))
        {
            priority += 50;
        }

        bool hasNonApipa = properties.UnicastAddresses.Any(address =>
            address.Address.AddressFamily == AddressFamily.InterNetwork &&
            !address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        if (!hasNonApipa)
        {
            priority += 15;
        }

        if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
        {
            priority -= 2;
        }
        else if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
        {
            priority -= 1;
        }

        return priority;
    }

    private static bool IsLikelyVirtualAdapter(NetworkInterface nic)
    {
        string value = $"{nic.Name} {nic.Description}".ToLowerInvariant();
        string[] markers = ["hyper-v", "virtual", "vmware", "virtualbox", "vpn", "tap", "tailscale", "zerotier", "loopback", "default switch"];
        return markers.Any(value.Contains);
    }

    private static IPInterfaceProperties? TryGetProperties(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties();
        }
        catch
        {
            return null;
        }
    }

    private static bool HasAnyIpv4(IPInterfaceProperties? properties)
    {
        return properties?.UnicastAddresses.Any(address =>
                   address.Address.AddressFamily == AddressFamily.InterNetwork) == true;
    }

    private static bool HasIpv4DefaultGateway(IPInterfaceProperties properties)
    {
        return properties.GatewayAddresses.Any(item =>
            item.Address.AddressFamily == AddressFamily.InterNetwork && item.Address.ToString() != "0.0.0.0");
    }

    private static async Task<WifiLinkDetails> ReadWifiLinkDetailsAsync(string adapterName, CancellationToken cancellationToken)
    {
        try
        {
            ProcessStartInfo startInfo = new("netsh.exe", "wlan show interfaces")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("netsh.exe could not be started.");
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            string[] sections = Regex.Split(output, @"(?im)(?=^\s*Name\s*:)");
            string section = sections.FirstOrDefault(candidate =>
            {
                Match nameMatch = Regex.Match(candidate, @"(?im)^\s*Name\s*:\s*(.+?)\s*$");
                return nameMatch.Success &&
                       string.Equals(nameMatch.Groups[1].Value.Trim(), adapterName, StringComparison.OrdinalIgnoreCase);
            }) ?? output;

            return new WifiLinkDetails(
                ParseDouble(section, @"(?im)^\s*Receive rate \(Mbps\)\s*:\s*([\d.,]+)\s*$"),
                ParseDouble(section, @"(?im)^\s*Transmit rate \(Mbps\)\s*:\s*([\d.,]+)\s*$"),
                ParseInt(section, @"(?im)^\s*Signal\s*:\s*(\d{1,3})\s*%\s*$"),
                ParseText(section, @"(?im)^\s*Radio type\s*:\s*(.+?)\s*$") ?? "Not reported",
                ParseText(section, @"(?im)^\s*Channel\s*:\s*(.+?)\s*$") ?? "Not reported");
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Read Wi-Fi link rate", exception);
            return WifiLinkDetails.Empty;
        }
    }

    private static double? ParseDouble(string input, string pattern)
    {
        Match match = Regex.Match(input, pattern);
        if (!match.Success)
        {
            return null;
        }

        string value = match.Groups[1].Value.Replace(',', '.');
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
    }

    private static int? ParseInt(string input, string pattern)
    {
        Match match = Regex.Match(input, pattern);
        return match.Success && int.TryParse(match.Groups[1].Value, out int parsed) ? parsed : null;
    }

    private static string? ParseText(string input, string pattern)
    {
        Match match = Regex.Match(input, pattern);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static async Task<double?> MeasureGatewayLatencyAsync(string gateway, CancellationToken cancellationToken)
    {
        try
        {
            using Ping ping = new();
            List<long> samples = new();
            for (int index = 0; index < 3; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PingReply reply = await ping.SendPingAsync(gateway, 800);
                if (reply.Status == IPStatus.Success)
                {
                    samples.Add(reply.RoundtripTime);
                }
            }

            return samples.Count == 0 ? null : samples.Average();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(double? ReceiveMbps, double? TransmitMbps)> MeasureCurrentTrafficAsync(
        NetworkInterface adapter,
        CancellationToken cancellationToken)
    {
        try
        {
            IPv4InterfaceStatistics before = adapter.GetIPv4Statistics();
            long receivedBefore = before.BytesReceived;
            long sentBefore = before.BytesSent;
            Stopwatch stopwatch = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            IPv4InterfaceStatistics after = adapter.GetIPv4Statistics();
            stopwatch.Stop();

            double seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            double receiveMbps = Math.Max(0, after.BytesReceived - receivedBefore) * 8.0 / seconds / 1_000_000.0;
            double transmitMbps = Math.Max(0, after.BytesSent - sentBefore) * 8.0 / seconds / 1_000_000.0;
            return (receiveMbps, transmitMbps);
        }
        catch
        {
            return (null, null);
        }
    }

    private sealed record WifiLinkDetails(
        double? ReceiveMbps,
        double? TransmitMbps,
        int? SignalPercent,
        string RadioType,
        string Channel)
    {
        public static WifiLinkDetails Empty { get; } = new(null, null, null, "Not reported", "Not reported");
    }
}
