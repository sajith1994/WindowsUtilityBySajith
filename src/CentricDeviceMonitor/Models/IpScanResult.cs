namespace CentricDeviceMonitor.Models;

public sealed class IpScanResult
{
    public string IpAddress { get; set; } = string.Empty;

    public string Hostname { get; set; } = "Not available";

    public string MacAddress { get; set; } = "Not available";

    public long? RoundTripTime { get; set; }

    public string OpenPortsDisplay { get; set; } = "Not scanned";

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
