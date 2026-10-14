namespace CentricDeviceMonitor.Models;

public sealed class IpScanResult
{
    public string IpAddress { get; set; } = string.Empty;

    public string Hostname { get; set; } = "Not available";

    public string MacAddress { get; set; } = "Not available";

    public long? RoundTripTime { get; set; }

    public string OpenPortsDisplay { get; set; } = "Not scanned";

    /// <summary>Port numbers found open by "Check open ports" (empty until checked).</summary>
    public List<int> OpenPorts { get; set; } = new();

    private static readonly (int Port, string Scheme)[] WebPorts =
    {
        (443, "https"), (80, "http"), (8443, "https"), (9443, "https"), (8080, "http"), (8000, "http"), (8008, "http"), (8888, "http"), (81, "http")
    };

    /// <summary>
    /// Web addresses to offer for this device: http:// and https:// on the IP, plus any other web port found open.
    /// Verified marks addresses whose port the port check found open.
    /// </summary>
    public IReadOnlyList<(string Url, bool Verified)> WebAddresses
    {
        get
        {
            List<(string Url, bool Verified)> addresses = new();
            foreach ((int port, string scheme) in WebPorts)
            {
                if (OpenPorts.Contains(port))
                {
                    bool defaultPort = (scheme == "https" && port == 443) || (scheme == "http" && port == 80);
                    addresses.Add((defaultPort ? $"{scheme}://{IpAddress}" : $"{scheme}://{IpAddress}:{port}", true));
                }
            }

            // Always offer both standard addresses, even before (or without) a port check.
            foreach (string url in new[] { $"http://{IpAddress}", $"https://{IpAddress}" })
            {
                if (addresses.All(address => address.Url != url))
                {
                    addresses.Add((url, false));
                }
            }

            return addresses;
        }
    }

    public string ResponseDisplay => RoundTripTime.HasValue ? $"{RoundTripTime.Value} ms" : "-";

    public string SuggestedName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Hostname) &&
                !string.Equals(Hostname, "Not available", StringComparison.OrdinalIgnoreCase))
            {
                return Hostname.Split('.')[0];
            }

            return $"Device {IpAddress}";
        }
    }
}
