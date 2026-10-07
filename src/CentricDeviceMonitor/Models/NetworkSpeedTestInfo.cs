namespace CentricDeviceMonitor.Models;

public sealed class NetworkLinkSnapshot
{
    public string AdapterName { get; init; } = "Not available";
    public string AdapterDescription { get; init; } = "Not available";
    public string InterfaceType { get; init; } = "Unknown";
    public string LocalIpAddress { get; init; } = "Not available";
    public string GatewayAddress { get; init; } = "Not available";
    public double? NegotiatedLinkMbps { get; init; }
    public double? WifiReceiveMbps { get; init; }
    public double? WifiTransmitMbps { get; init; }
    public int? WifiSignalPercent { get; init; }
    public string WifiRadioType { get; init; } = "Not reported";
    public string WifiChannel { get; init; } = "Not reported";
    public double? GatewayLatencyMs { get; init; }
    public double? CurrentReceiveMbps { get; init; }
    public double? CurrentTransmitMbps { get; init; }
    public bool InternetAvailable { get; init; }
    public string InternetStatus { get; init; } = "Not checked";

    public string CompactLinkDisplay
    {
        get
        {
            if (InterfaceType.Equals("Wi-Fi", StringComparison.OrdinalIgnoreCase) &&
                (WifiReceiveMbps.HasValue || WifiTransmitMbps.HasValue))
            {
                string rx = WifiReceiveMbps.HasValue ? $"Rx {WifiReceiveMbps.Value:0.#} Mbps" : "Rx --";
                string tx = WifiTransmitMbps.HasValue ? $"Tx {WifiTransmitMbps.Value:0.#} Mbps" : "Tx --";
                return $"Wi-Fi • {rx} • {tx}";
            }

            return NegotiatedLinkMbps.HasValue
                ? $"{InterfaceType} • {FormatMbps(NegotiatedLinkMbps.Value)}"
                : $"{InterfaceType} • link rate unavailable";
        }
    }

    public static string FormatMbps(double mbps)
    {
        return mbps >= 1000
            ? $"{mbps / 1000.0:0.##} Gbps"
            : $"{mbps:0.#} Mbps";
    }
}
