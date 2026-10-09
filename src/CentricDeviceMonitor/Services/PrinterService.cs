using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CentricDeviceMonitor.Services;

/// <summary>One row in the Print Spooler tile's printer lists.</summary>
public sealed record PrinterListItem(string Title, string Detail);

public sealed record InstalledPrinterInfo(
    string Name,
    bool IsDefault,
    bool IsNetwork,
    bool IsVirtual,
    string Status,
    string PortName,
    string? Address,
    string? DeviceName = null,
    string? MacAddress = null);

/// <summary>A WSD printer as Windows recorded it when the device was discovered.</summary>
internal sealed record WsdDeviceInfo(string DeviceName, string FriendlyName, string? IPv4Address, string? MacAddress);

public sealed record NetworkPrinterInfo(string Address, string HostName, IReadOnlyList<string> Services, bool IsInstalled);

/// <summary>
/// Lists printers installed on this PC (WMI Win32_Printer) and finds printers on the local subnet by
/// probing the standard printing ports. The network probe is a plain TCP connect, nothing is sent.
/// </summary>
public sealed class PrinterService
{
    private static readonly (int Port, string Name)[] PrinterPorts =
    {
        (9100, "RAW"),
        (631, "IPP"),
        (515, "LPD")
    };

    private static readonly Regex ArpLineRegex = new(
        @"^\s*(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s+(?<mac>[0-9A-Fa-f]{2}(?:-[0-9A-Fa-f]{2}){5})\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly string[] VirtualPortPrefixes =
    {
        "PORTPROMPT:", "NUL:", "FILE:", "SHRFAX:", "ONENOTE", "XPS", "PDF", "MICROSOFT.OFFICE", "AD_PORT"
    };

    private readonly LocalIpScannerService _subnetService = new();

    public Task<IReadOnlyList<InstalledPrinterInfo>> GetInstalledPrintersAsync() => Task.Run(GetInstalledPrinters);

    private static IReadOnlyList<InstalledPrinterInfo> GetInstalledPrinters()
    {
        Dictionary<string, string> tcpPorts = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using ManagementObjectSearcher portSearcher = new("SELECT Name, HostAddress FROM Win32_TCPIPPrinterPort");
            foreach (ManagementBaseObject port in portSearcher.Get())
            {
                using (port)
                {
                    string? name = port["Name"] as string;
                    string? host = port["HostAddress"] as string;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(host))
                    {
                        tcpPorts[name] = host.Trim();
                    }
                }
            }
        }
        catch
        {
            // Port details are optional; printers are still listed without an address.
        }

        IReadOnlyList<WsdDeviceInfo> wsdDevices = ReadWsdDevices();

        List<InstalledPrinterInfo> printers = new();
        using ManagementObjectSearcher searcher = new(
            "SELECT Name, Default, Network, WorkOffline, PrinterStatus, PortName, ServerName FROM Win32_Printer");
        foreach (ManagementBaseObject printer in searcher.Get())
        {
            using (printer)
            {
                string name = printer["Name"] as string ?? "Unnamed printer";
                string portName = printer["PortName"] as string ?? string.Empty;
                bool isNetwork = printer["Network"] is true;
                bool offline = printer["WorkOffline"] is true;
                int status = printer["PrinterStatus"] is ushort value ? value : 2;
                string? server = printer["ServerName"] as string;

                string? address = tcpPorts.TryGetValue(portName, out string? host)
                    ? host
                    : !string.IsNullOrWhiteSpace(server) ? server.TrimStart('\\') : null;
                if (address == "0.0.0.0")
                {
                    address = null;
                }

                // WSD ports carry no address; use the device Windows discovered under a matching name.
                bool isWsd = portName.StartsWith("WSD", StringComparison.OrdinalIgnoreCase);
                WsdDeviceInfo? wsd = isWsd
                    ? wsdDevices.FirstOrDefault(device =>
                        name.Contains(device.DeviceName, StringComparison.OrdinalIgnoreCase)
                        || name.Equals(device.FriendlyName, StringComparison.OrdinalIgnoreCase))
                    : null;
                address ??= wsd?.IPv4Address;

                printers.Add(new InstalledPrinterInfo(
                    name,
                    printer["Default"] is true,
                    isNetwork || isWsd || address is not null,
                    IsVirtualPort(portName),
                    offline ? "Offline" : DescribeStatus(status),
                    isWsd ? "WSD" : portName,
                    address,
                    wsd?.DeviceName,
                    wsd?.MacAddress));
            }
        }

        return printers
            .OrderByDescending(printer => printer.IsDefault)
            .ThenBy(printer => printer.IsVirtual)
            .ThenBy(printer => printer.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Probes the local /24 (or narrower) subnet for hosts that accept printing connections.</summary>
    public async Task<IReadOnlyList<NetworkPrinterInfo>> ScanNetworkPrintersAsync(
        IReadOnlyCollection<InstalledPrinterInfo> installed,
        CancellationToken cancellationToken = default)
    {
        LocalSubnetInfo subnet = _subnetService.GetLocalSubnet()
            ?? throw new InvalidOperationException("No active Ethernet or Wi-Fi network was found to scan.");

        HashSet<string> installedAddresses = await ResolveInstalledAddressesAsync(installed, cancellationToken);
        HashSet<string> installedMacs = installed
            .Select(printer => printer.MacAddress)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> installedNames = installed
            .Where(printer => !printer.IsVirtual)
            .SelectMany(printer => new[] { printer.Name, printer.DeviceName })
            .OfType<string>()
            .ToList();
        ConcurrentBag<NetworkPrinterInfo> found = new();

        await Parallel.ForEachAsync(
            subnet.Addresses,
            new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = cancellationToken },
            async (address, token) =>
            {
                List<string> services = new();
                foreach ((int port, string name) in PrinterPorts)
                {
                    if (await IsPortOpenAsync(address, port, token))
                    {
                        services.Add(name);
                    }
                }

                if (services.Count == 0)
                {
                    return;
                }

                string hostName = await TryGetHostNameAsync(address, token);
                found.Add(new NetworkPrinterInfo(address, hostName, services, false));
            });

        // The connection attempts above put every responding printer into the ARP cache.
        Dictionary<string, string> macByAddress = ReadArpTable();

        return found
            .Select(printer =>
            {
                string shortName = printer.HostName.Split('.')[0];
                bool isInstalled = installedAddresses.Contains(printer.Address)
                    || (printer.HostName.Length > 0 && installedAddresses.Contains(printer.HostName))
                    || (macByAddress.TryGetValue(printer.Address, out string? mac) && installedMacs.Contains(mac))
                    || (shortName.Length >= 4 && installedNames.Any(name => name.Contains(shortName, StringComparison.OrdinalIgnoreCase)));
                return printer with { IsInstalled = isInstalled };
            })
            .OrderBy(printer => printer.IsInstalled)
            .ThenBy(printer => IPAddress.Parse(printer.Address).GetAddressBytes()[3])
            .ToList();
    }

    private static async Task<HashSet<string>> ResolveInstalledAddressesAsync(
        IEnumerable<InstalledPrinterInfo> installed,
        CancellationToken cancellationToken)
    {
        HashSet<string> addresses = new(StringComparer.OrdinalIgnoreCase);
        foreach (string address in installed.Select(printer => printer.Address).OfType<string>())
        {
            addresses.Add(address);
            if (IPAddress.TryParse(address, out _))
            {
                continue;
            }

            // Ports created by host name: add the IPs so the scan can match them.
            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                foreach (IPAddress ip in await Dns.GetHostAddressesAsync(address, timeout.Token))
                {
                    addresses.Add(ip.ToString());
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Unresolvable names simply cannot be matched.
            }
        }

        return addresses;
    }

    private static async Task<bool> IsPortOpenAsync(string address, int port, CancellationToken cancellationToken)
    {
        using TcpClient client = new();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<string> TryGetHostNameAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
            IPHostEntry entry = await Dns.GetHostEntryAsync(address, timeout.Token);
            return entry.HostName.Equals(address, StringComparison.Ordinal) ? string.Empty : entry.HostName;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// WSD printers discovered by Windows (Function Discovery). FriendlyName looks like "HP0EA67B (HP DeskJet 2900
    /// series)"; LocationInformation is the device URL, often an IPv6 link-local address built from the MAC.
    /// </summary>
    private static IReadOnlyList<WsdDeviceInfo> ReadWsdDevices()
    {
        List<WsdDeviceInfo> devices = new();
        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\SWD\DAFWSDProvider");
            if (root is null)
            {
                return devices;
            }

            foreach (string subKeyName in root.GetSubKeyNames())
            {
                using RegistryKey? device = root.OpenSubKey(subKeyName);
                string friendlyName = device?.GetValue("FriendlyName") as string ?? string.Empty;
                string location = device?.GetValue("LocationInformation") as string ?? string.Empty;
                if (string.IsNullOrWhiteSpace(friendlyName)
                    || !Uri.TryCreate(location, UriKind.Absolute, out Uri? uri)
                    || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? ip))
                {
                    continue;
                }

                string deviceName = friendlyName.Split(' ', 2)[0];
                devices.Add(new WsdDeviceInfo(
                    deviceName,
                    friendlyName,
                    ip.AddressFamily == AddressFamily.InterNetwork ? ip.ToString() : null,
                    ip.AddressFamily == AddressFamily.InterNetworkV6 ? MacFromEui64(ip) : null));
            }
        }
        catch
        {
            // Without WSD details, WSD printers are listed but cannot be matched to scan results.
        }

        return devices;
    }

    /// <summary>Recovers the MAC from an EUI-64 IPv6 interface id (xx:xx:xx:ff:fe:xx:xx:xx).</summary>
    private static string? MacFromEui64(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 16 || bytes[11] != 0xFF || bytes[12] != 0xFE)
        {
            return null;
        }

        byte[] mac = { (byte)(bytes[8] ^ 0x02), bytes[9], bytes[10], bytes[13], bytes[14], bytes[15] };
        return string.Join("-", mac.Select(value => value.ToString("x2")));
    }

    private static Dictionary<string, string> ReadArpTable()
    {
        Dictionary<string, string> table = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using Process process = Process.Start(new ProcessStartInfo("arp.exe", "-a")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("arp.exe did not start.");

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            foreach (Match match in ArpLineRegex.Matches(output))
            {
                table[match.Groups["ip"].Value] = match.Groups["mac"].Value.ToLowerInvariant();
            }
        }
        catch
        {
            // MAC matching is a best-effort extra.
        }

        return table;
    }

    private static bool IsVirtualPort(string portName)
    {
        string upper = portName.Trim().ToUpperInvariant();
        return VirtualPortPrefixes.Any(prefix => upper.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string DescribeStatus(int status) => status switch
    {
        3 => "Ready",
        4 => "Printing",
        5 => "Warming up",
        6 => "Stopped",
        7 => "Offline",
        _ => "Status unknown"
    };
}
