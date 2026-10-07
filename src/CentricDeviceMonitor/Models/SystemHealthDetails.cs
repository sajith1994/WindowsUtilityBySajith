namespace CentricDeviceMonitor.Models;

public sealed class SystemHealthDetails
{
    public string WindowsDisplay { get; init; } = "Windows information unavailable";

    public string BatteryDisplay { get; init; } = "No battery detected";

    public string WifiDisplay { get; init; } = "Wi-Fi not connected";

    public string LocalIpDisplay { get; init; } = "Not available";

    public string GatewayDnsDisplay { get; init; } = "Not available";

    public string PublicIpDisplay { get; init; } = "Not available";

    public string PublicLocationDisplay { get; init; } = "Not available";

    public string PublicServerDisplay { get; init; } = "Not available";

    public DateTime UpdatedAt { get; init; } = DateTime.Now;
}
