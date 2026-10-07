using System.Diagnostics;
using System.Management;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using CentricDeviceMonitor.Models;
using Microsoft.Win32;

namespace CentricDeviceMonitor.Services;

public sealed class SystemHealthDetailsService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly TimeSpan PublicLookupCacheDuration = TimeSpan.FromMinutes(5);

    private readonly object _cacheLock = new();
    private PublicNetworkInfo? _cachedPublicNetworkInfo;
    private DateTime _cachedPublicNetworkInfoAt = DateTime.MinValue;
    private readonly string _windowsDisplay;

    public SystemHealthDetailsService()
    {
        _windowsDisplay = ReadWindowsDisplay();
    }

    public async Task<SystemHealthDetails> GetAsync(bool forcePublicRefresh = false, CancellationToken cancellationToken = default)
    {
        string battery = ReadBatteryDisplay();
        string wifi = await ReadWifiDisplayAsync(cancellationToken);
        (string localIp, string gatewayDns) = ReadPreferredNetworkDetails();
        PublicNetworkInfo publicInfo = await GetPublicNetworkInfoAsync(forcePublicRefresh, cancellationToken);

        return new SystemHealthDetails
        {
            WindowsDisplay = _windowsDisplay,
            BatteryDisplay = battery,
            WifiDisplay = wifi,
            LocalIpDisplay = localIp,
            GatewayDnsDisplay = gatewayDns,
            PublicIpDisplay = publicInfo.IpAddress,
            PublicLocationDisplay = publicInfo.Location,
            PublicServerDisplay = publicInfo.Server,
            UpdatedAt = DateTime.Now
        };
    }

    private async Task<PublicNetworkInfo> GetPublicNetworkInfoAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (!forceRefresh &&
                _cachedPublicNetworkInfo is not null &&
                DateTime.UtcNow - _cachedPublicNetworkInfoAt < PublicLookupCacheDuration)
            {
                return _cachedPublicNetworkInfo;
            }
        }

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            string json = await HttpClient.GetStringAsync("https://ipwho.is/", timeout.Token);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            bool success = !root.TryGetProperty("success", out JsonElement successElement) || successElement.GetBoolean();
            if (!success)
            {
                throw new InvalidOperationException("Public IP lookup service returned an unsuccessful response.");
            }

            string ip = GetString(root, "ip") ?? "Not available";
            string city = GetString(root, "city") ?? string.Empty;
            string region = GetString(root, "region") ?? string.Empty;
            string country = GetString(root, "country") ?? string.Empty;
            string location = JoinNonEmpty(", ", city, region, country);
            if (string.IsNullOrWhiteSpace(location))
            {
                location = "Location not reported";
            }

            string server = "ISP not reported";
            if (root.TryGetProperty("connection", out JsonElement connection))
            {
                string isp = GetString(connection, "isp") ?? string.Empty;
                string org = GetString(connection, "org") ?? string.Empty;
                string domain = GetString(connection, "domain") ?? string.Empty;
                string asn = connection.TryGetProperty("asn", out JsonElement asnElement) && asnElement.ValueKind == JsonValueKind.Number
                    ? $"AS{asnElement.GetInt64()}"
                    : string.Empty;

                server = JoinNonEmpty(" • ", isp, asn, org, domain);
                if (string.IsNullOrWhiteSpace(server))
                {
                    server = "ISP not reported";
                }
            }

            PublicNetworkInfo current = new(ip, location, $"{server} • lookup: ipwho.is");
            lock (_cacheLock)
            {
                _cachedPublicNetworkInfo = current;
                _cachedPublicNetworkInfoAt = DateTime.UtcNow;
            }

            return current;
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Public IP/location lookup", exception);
            lock (_cacheLock)
            {
                PublicNetworkInfo fallback = _cachedPublicNetworkInfo is not null
                    ? _cachedPublicNetworkInfo with { Server = _cachedPublicNetworkInfo.Server + " • cached" }
                    : new PublicNetworkInfo("Unavailable", "Unavailable while offline", "Public lookup unavailable");
                _cachedPublicNetworkInfo = fallback;
                _cachedPublicNetworkInfoAt = DateTime.UtcNow;
                return fallback;
            }
        }
    }

    private static string ReadWindowsDisplay()
    {
        try
        {
            string caption = string.Empty;
            string build = string.Empty;
            string version = string.Empty;
            using ManagementObjectSearcher searcher = new("SELECT Caption, BuildNumber, Version FROM Win32_OperatingSystem");
            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                caption = item["Caption"]?.ToString()?.Trim() ?? string.Empty;
                build = item["BuildNumber"]?.ToString()?.Trim() ?? string.Empty;
                version = item["Version"]?.ToString()?.Trim() ?? string.Empty;
                break;
            }

            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string displayVersion = key?.GetValue("DisplayVersion")?.ToString()?.Trim() ?? string.Empty;
            string ubr = key?.GetValue("UBR")?.ToString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(build) && !string.IsNullOrWhiteSpace(ubr))
            {
                build += "." + ubr;
            }

            string baseText = JoinNonEmpty(" ", caption, displayVersion);
            if (string.IsNullOrWhiteSpace(baseText))
            {
                baseText = string.IsNullOrWhiteSpace(version) ? "Windows" : $"Windows {version}";
            }

            return string.IsNullOrWhiteSpace(build) ? baseText : $"{baseText} • build {build}";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Windows version details", exception);
            return $"Windows {Environment.OSVersion.Version} • build {Environment.OSVersion.Version.Build}";
        }
    }

    private static string ReadBatteryDisplay()
    {
        try
        {
            System.Windows.Forms.PowerStatus power = System.Windows.Forms.SystemInformation.PowerStatus;
            if (power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) ||
                power.BatteryLifePercent < 0)
            {
                return "No battery detected";
            }

            int percent = (int)Math.Round(Math.Clamp(power.BatteryLifePercent, 0f, 1f) * 100.0);
            string state = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online
                ? power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging)
                    ? "charging"
                    : "plugged in"
                : "on battery";
            return $"{percent}% • {state}";
        }
        catch
        {
            return "Battery status unavailable";
        }
    }

    private static async Task<string> ReadWifiDisplayAsync(CancellationToken cancellationToken)
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

            Match signal = Regex.Match(output, @"(?im)^\s*Signal\s*:\s*(\d{1,3})\s*%\s*$");
            Match ssid = Regex.Match(output, @"(?im)^\s*SSID\s*:\s*(.+?)\s*$");
            if (!signal.Success)
            {
                return "Wi-Fi not connected";
            }

            int quality = Math.Clamp(int.Parse(signal.Groups[1].Value), 0, 100);
            string profile = ssid.Success ? ssid.Groups[1].Value.Trim() : string.Empty;
            return string.IsNullOrWhiteSpace(profile) ? $"{quality}% signal" : $"{quality}% • {profile}";
        }
        catch
        {
            return "Wi-Fi strength unavailable";
        }
    }

    private static (string LocalIp, string GatewayDns) ReadPreferredNetworkDetails()
    {
        try
        {
            List<(int Priority, string LocalIp, string GatewayDns)> candidates = new();
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
                catch
                {
                    continue;
                }

                string? ipv4 = properties.UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => address.Address.ToString())
                    .FirstOrDefault(address => !address.StartsWith("169.254.", StringComparison.Ordinal));
                if (string.IsNullOrWhiteSpace(ipv4))
                {
                    continue;
                }

                string gateway = properties.GatewayAddresses
                    .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(item => item.Address.ToString())
                    .FirstOrDefault(address => address != "0.0.0.0") ?? string.Empty;
                string dns = properties.DnsAddresses
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => address.ToString())
                    .FirstOrDefault() ?? string.Empty;

                int priority = string.IsNullOrWhiteSpace(gateway) ? 10 : 0;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    priority -= 2;
                }
                else if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                {
                    priority -= 1;
                }

                string type = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : nic.NetworkInterfaceType.ToString();
                string gatewayDns = JoinNonEmpty(" • ",
                    string.IsNullOrWhiteSpace(gateway) ? string.Empty : $"Gateway {gateway}",
                    string.IsNullOrWhiteSpace(dns) ? string.Empty : $"DNS {dns}");
                candidates.Add((priority, $"{ipv4} • {type}", string.IsNullOrWhiteSpace(gatewayDns) ? "Gateway/DNS not reported" : gatewayDns));
            }

            var selected = candidates.OrderBy(item => item.Priority).FirstOrDefault();
            return string.IsNullOrWhiteSpace(selected.LocalIp)
                ? ("No IPv4 address", "Gateway/DNS unavailable")
                : (selected.LocalIp, selected.GatewayDns);
        }
        catch
        {
            return ("Local IP unavailable", "Gateway/DNS unavailable");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"WindowsUtilityBySajith/{typeof(SystemHealthDetailsService).Assembly.GetName().Version?.ToString(3) ?? "unknown"}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string JoinNonEmpty(string separator, params string[] values) =>
        string.Join(separator, values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private sealed record PublicNetworkInfo(string IpAddress, string Location, string Server);
}
