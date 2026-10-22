using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WindowsUtility.Services;

/// <summary>One network connection that is up and has a usable IPv4 address.</summary>
public sealed record ActiveConnection(
    string Name,
    string Kind,
    string Description,
    string LinkDisplay,
    string AddressDisplay,
    bool HasGateway,
    bool UsedForInternet,
    bool IsVirtual)
{
    public string Headline => $"{Name}  •  {Kind}";
}

/// <summary>
/// Lists every active connection (Wi-Fi, Ethernet, mobile broadband, VPN) with its address, gateway, DNS and link
/// details, and marks the one Windows routes internet traffic through. Virtual switches without a gateway (Hyper-V
/// Default Switch, WSL, VM host-only networks) are left out: they never carry the PC's own traffic.
/// </summary>
public static class ActiveConnectionsService
{
    public static async Task<IReadOnlyList<ActiveConnection>> GetAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, Dictionary<string, string>> wifi = await ReadWifiInterfacesAsync(cancellationToken);
        return await Task.Run(() => Read(wifi), cancellationToken);
    }

    private static IReadOnlyList<ActiveConnection> Read(Dictionary<string, Dictionary<string, string>> wifi)
    {
        int? internetIndex = null;
        try
        {
            uint destination = BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0);
            if (GetBestInterface(destination, out uint index) == 0)
            {
                internetIndex = (int)index;
            }
        }
        catch (Exception)
        {
            // Without a route lookup the list is still shown, just without the Internet badge.
        }

        List<ActiveConnection> connections = new();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            List<string> addresses = properties.UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address.Address)
                    && !address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(address => $"{address.Address}/{address.PrefixLength}")
                .ToList();
            if (addresses.Count == 0)
            {
                continue;
            }

            List<string> gateways = properties.GatewayAddresses
                .Select(gateway => gateway.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any))
                .Select(address => address.ToString())
                .ToList();
            bool isVirtual = SystemResourceMonitorService.IsVirtualAdapter(nic);
            if (isVirtual && gateways.Count == 0)
            {
                continue;
            }

            int? interfaceIndex = null;
            try
            {
                interfaceIndex = properties.GetIPv4Properties()?.Index;
            }
            catch (NetworkInformationException)
            {
            }

            List<string> dns = properties.DnsAddresses
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.ToString())
                .ToList();

            string kind = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile broadband",
                NetworkInterfaceType.Ppp => "VPN / dial-up",
                _ when isVirtual => "VPN / virtual",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                _ => nic.NetworkInterfaceType.ToString()
            };

            string link = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && wifi.TryGetValue(nic.Name, out Dictionary<string, string>? radio)
                ? WifiLinkDisplay(radio, nic.Speed)
                : FormatSpeed(nic.Speed);

            string addressDisplay = string.Join("  •  ", new[]
            {
                $"IP {string.Join(", ", addresses)}",
                gateways.Count > 0 ? $"Gateway {string.Join(", ", gateways)}" : "No gateway (local network only)",
                dns.Count > 0 ? $"DNS {string.Join(", ", dns)}" : string.Empty
            }.Where(part => part.Length > 0));

            connections.Add(new ActiveConnection(
                nic.Name,
                kind,
                nic.Description,
                link,
                addressDisplay,
                gateways.Count > 0,
                interfaceIndex is not null && interfaceIndex == internetIndex,
                isVirtual));
        }

        return connections
            .OrderByDescending(connection => connection.UsedForInternet)
            .ThenByDescending(connection => connection.HasGateway)
            .ThenBy(connection => connection.IsVirtual)
            .ThenBy(connection => connection.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string WifiLinkDisplay(Dictionary<string, string> radio, long speed)
    {
        radio.TryGetValue("SSID", out string? ssid);
        radio.TryGetValue("Signal", out string? signal);
        radio.TryGetValue("Receive rate (Mbps)", out string? receive);
        radio.TryGetValue("Transmit rate (Mbps)", out string? transmit);
        string rates = !string.IsNullOrWhiteSpace(receive) && !string.IsNullOrWhiteSpace(transmit)
            ? $"Rx {receive} / Tx {transmit} Mbps"
            : FormatSpeed(speed);
        return string.Join("  •  ", new[]
        {
            string.IsNullOrWhiteSpace(ssid) ? string.Empty : ssid,
            string.IsNullOrWhiteSpace(signal) ? string.Empty : $"{signal} signal",
            rates
        }.Where(part => part.Length > 0));
    }

    private static string FormatSpeed(long bitsPerSecond) => bitsPerSecond switch
    {
        <= 0 => "Link speed not reported",
        >= 1_000_000_000 => $"{bitsPerSecond / 1_000_000_000d:0.#} Gbps link",
        >= 1_000_000 => $"{bitsPerSecond / 1_000_000d:0.#} Mbps link",
        _ => $"{bitsPerSecond / 1_000d:0} Kbps link"
    };

    /// <summary>Per-interface Wi-Fi details from netsh (SSID, signal, rates), keyed by interface name.</summary>
    private static async Task<Dictionary<string, Dictionary<string, string>>> ReadWifiInterfacesAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, Dictionary<string, string>> interfaces = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            ProcessStartInfo startInfo = new("netsh.exe", "wlan show interfaces")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("netsh.exe could not be started.");
            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            Dictionary<string, string>? current = null;
            foreach (string line in output.Split('\n'))
            {
                Match match = Regex.Match(line, @"^\s*(.+?)\s*:\s*(.*?)\s*$");
                if (!match.Success)
                {
                    continue;
                }

                string key = match.Groups[1].Value;
                string value = match.Groups[2].Value;
                if (key.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    interfaces[value] = current;
                }
                else if (current is not null && !current.ContainsKey(key))
                {
                    current[key] = value;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Wi-Fi rows then show the adapter's link speed only.
        }

        return interfaces;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);
}
