using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace CentricDeviceMonitor.Services;

/// <summary>Current IPv4 configuration of one network adapter.</summary>
public sealed record NetworkAdapterInfo(
    string Name,
    string Description,
    string Status,
    bool DhcpEnabled,
    string IPv4Address,
    string SubnetMask,
    string Gateway,
    IReadOnlyList<string> DnsServers,
    bool DnsFromDhcp)
{
    public string DisplayName => $"{Name}  -  {Description}  ({Status})";

    public string Summary
    {
        get
        {
            string address = string.IsNullOrEmpty(IPv4Address) ? "no IPv4 address" : $"{IPv4Address} / {SubnetMask}";
            string gateway = string.IsNullOrEmpty(Gateway) ? "no gateway" : $"gateway {Gateway}";
            string dns = DnsServers.Count == 0 ? "no DNS servers" : string.Join(", ", DnsServers);
            return $"{(DhcpEnabled ? "Automatic (DHCP)" : "Static")}  •  {address}  •  {gateway}  •  DNS {dns}{(DnsFromDhcp ? " (automatic)" : " (manual)")}";
        }
    }
}

/// <summary>A DNS choice offered in the adapter settings card. Null servers mean "automatic from DHCP".</summary>
public sealed record DnsPreset(string Name, string? Primary, string? Secondary, string Note)
{
    public bool IsAutomatic => Primary is null && Name.StartsWith("Automatic", StringComparison.Ordinal);

    public bool IsCustom => Primary is null && !IsAutomatic;

    public override string ToString() => Primary is null ? Name : $"{Name}  ({Primary}, {Secondary})";
}

/// <summary>
/// Reads adapters with System.Net.NetworkInformation and changes IPv4 address/DNS settings with netsh.
/// Requires administrator rights (the dashboard has them). Changing these settings can drop the connection,
/// so the caller must confirm with the user first, especially in remote sessions.
/// </summary>
public sealed class NetworkAdapterConfigService
{
    public static IReadOnlyList<DnsPreset> DnsPresets { get; } = new[]
    {
        new DnsPreset("Automatic (from router / DHCP)", null, null, "Uses whatever DNS the network hands out. The usual default."),
        new DnsPreset("Google Public DNS", "8.8.8.8", "8.8.4.4", "Fast, reliable public DNS. No filtering."),
        new DnsPreset("Cloudflare", "1.1.1.1", "1.0.0.1", "Fast, privacy-focused public DNS. No filtering."),
        new DnsPreset("Cloudflare for Families", "1.1.1.3", "1.0.0.3", "Blocks malware and adult content."),
        new DnsPreset("Quad9", "9.9.9.9", "149.112.112.112", "Blocks known malicious domains."),
        new DnsPreset("OpenDNS", "208.67.222.222", "208.67.220.220", "Cisco public DNS."),
        new DnsPreset("AdGuard DNS", "94.140.14.14", "94.140.15.15", "Blocks ads and trackers."),
        new DnsPreset("Custom", null, null, "Enter your own preferred and alternate DNS servers.")
    };

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        List<NetworkAdapterInfo> adapters = new();
        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || !networkInterface.Supports(NetworkInterfaceComponent.IPv4))
            {
                continue;
            }

            try
            {
                IPInterfaceProperties properties = networkInterface.GetIPProperties();
                IPv4InterfaceProperties? ipv4 = properties.GetIPv4Properties();
                UnicastIPAddressInformation? unicast = properties.UnicastAddresses
                    .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork);
                string gateway = properties.GatewayAddresses
                    .Select(address => address.Address)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !address.Equals(IPAddress.Any))?.ToString() ?? string.Empty;
                List<string> dns = properties.DnsAddresses
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => address.ToString())
                    .ToList();

                adapters.Add(new NetworkAdapterInfo(
                    networkInterface.Name,
                    networkInterface.Description,
                    networkInterface.OperationalStatus == OperationalStatus.Up ? "Connected" : "Disconnected",
                    ipv4?.IsDhcpEnabled ?? false,
                    unicast?.Address.ToString() ?? string.Empty,
                    unicast?.IPv4Mask?.ToString() ?? string.Empty,
                    gateway,
                    dns,
                    IsDnsFromDhcp(networkInterface.Id)));
            }
            catch
            {
                // Skip adapters Windows cannot describe.
            }
        }

        return adapters
            .OrderByDescending(adapter => adapter.Status == "Connected")
            .ThenByDescending(adapter => !string.IsNullOrEmpty(adapter.Gateway))
            .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Manually set DNS servers are stored in the interface's NameServer value; empty means DHCP-provided.</summary>
    private static bool IsDnsFromDhcp(string interfaceId)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{interfaceId}");
            return string.IsNullOrWhiteSpace(key?.GetValue("NameServer") as string);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Validates the requested settings and returns a list of problems (empty when valid).</summary>
    public static IReadOnlyList<string> Validate(bool useDhcp, string address, string mask, string gateway, DnsPreset dns, string primaryDns, string secondaryDns)
    {
        List<string> problems = new();
        if (!useDhcp)
        {
            if (!TryParseIPv4(address, out IPAddress? ip))
            {
                problems.Add("Enter a valid IPv4 address, for example 192.168.1.50.");
            }

            if (!TryParseIPv4(mask, out IPAddress? subnet) || !IsValidMask(subnet!))
            {
                problems.Add("Enter a valid subnet mask, for example 255.255.255.0.");
            }
            else if (ip is not null)
            {
                uint host = ToUInt32(ip) & ~ToUInt32(subnet!);
                if (host == 0 || host == ~ToUInt32(subnet!))
                {
                    problems.Add("The IP address cannot be the network or broadcast address of the subnet.");
                }

                if (!string.IsNullOrWhiteSpace(gateway))
                {
                    if (!TryParseIPv4(gateway, out IPAddress? gw))
                    {
                        problems.Add("Enter a valid default gateway, for example 192.168.1.1, or leave it empty.");
                    }
                    else if ((ToUInt32(gw!) & ToUInt32(subnet!)) != (ToUInt32(ip) & ToUInt32(subnet!)))
                    {
                        problems.Add("The default gateway must be in the same subnet as the IP address.");
                    }
                }
            }
        }

        if (!dns.IsAutomatic)
        {
            if (!TryParseIPv4(primaryDns, out _))
            {
                problems.Add("Enter a valid preferred DNS server, for example 8.8.8.8.");
            }

            if (!string.IsNullOrWhiteSpace(secondaryDns) && !TryParseIPv4(secondaryDns, out _))
            {
                problems.Add("Enter a valid alternate DNS server, or leave it empty.");
            }
        }

        return problems;
    }

    /// <summary>Applies the settings with netsh. Returns a description of what changed.</summary>
    public async Task<string> ApplyAsync(NetworkAdapterInfo adapter, bool useDhcp, string address, string mask, string gateway,
        bool automaticDns, string primaryDns, string secondaryDns)
    {
        string adapterName = adapter.Name;
        if (string.IsNullOrWhiteSpace(adapterName) || adapterName.Contains('"'))
        {
            throw new InvalidOperationException("The selected adapter name is not usable.");
        }

        string name = $"name=\"{adapterName}\"";
        List<string> done = new();

        if (useDhcp)
        {
            if (!adapter.DhcpEnabled)
            {
                await RunNetshAsync($"interface ipv4 set address {name} source=dhcp", tolerateAlreadyDhcp: true);
            }

            done.Add("IP address set to automatic (DHCP)");
        }
        else
        {
            string ip = Normalize(address);
            string subnet = Normalize(mask);
            string gw = string.IsNullOrWhiteSpace(gateway) ? "none" : Normalize(gateway);
            await RunNetshAsync($"interface ipv4 set address {name} static {ip} {subnet} {gw}");
            done.Add($"static IP {ip} / {subnet}{(gw == "none" ? " (no gateway)" : $", gateway {gw}")}");
        }

        if (automaticDns)
        {
            if (!adapter.DnsFromDhcp)
            {
                await RunNetshAsync($"interface ipv4 set dnsservers {name} source=dhcp", tolerateAlreadyDhcp: true);
            }

            done.Add("DNS set to automatic");
        }
        else
        {
            string primary = Normalize(primaryDns);
            await RunNetshAsync($"interface ipv4 set dnsservers {name} static {primary} primary validate=no");
            string dnsText = primary;
            if (!string.IsNullOrWhiteSpace(secondaryDns))
            {
                string secondary = Normalize(secondaryDns);
                await RunNetshAsync($"interface ipv4 add dnsservers {name} {secondary} index=2 validate=no");
                dnsText += $", {secondary}";
            }

            done.Add($"DNS {dnsText}");
        }

        // Make the new DNS servers take effect for cached names straight away.
        await RunProcessAsync("ipconfig.exe", "/flushdns");
        return $"{adapterName}: {string.Join("; ", done)}.";
    }

    private static async Task RunNetshAsync(string arguments, bool tolerateAlreadyDhcp = false)
    {
        (int exitCode, string output) = await RunProcessAsync("netsh.exe", arguments);
        if (exitCode != 0)
        {
            // netsh reports an error when asked to enable DHCP on an adapter that already uses it.
            if (tolerateAlreadyDhcp && output.Contains("DHCP is already enabled", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidOperationException(string.IsNullOrWhiteSpace(output)
                ? $"netsh {arguments} failed with exit code {exitCode}."
                : output.Trim());
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments)
    {
        ProcessStartInfo startInfo = new(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{fileName} could not be started.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, ((await output) + Environment.NewLine + (await error)).Trim());
    }

    private static string Normalize(string value) =>
        TryParseIPv4(value, out IPAddress? ip) ? ip!.ToString() : throw new InvalidOperationException($"'{value}' is not a valid IPv4 address.");

    private static bool TryParseIPv4(string? value, out IPAddress? address)
    {
        address = null;
        string text = value?.Trim() ?? string.Empty;
        // IPAddress.TryParse accepts shorthand like "10.1"; require four dotted parts.
        if (text.Count(character => character == '.') != 3
            || !IPAddress.TryParse(text, out IPAddress? parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private static bool IsValidMask(IPAddress mask)
    {
        uint value = ToUInt32(mask);
        uint inverted = ~value;
        return value != 0 && (inverted & (inverted + 1)) == 0;
    }

    private static uint ToUInt32(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
