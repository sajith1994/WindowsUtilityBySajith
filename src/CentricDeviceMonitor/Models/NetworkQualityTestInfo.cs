namespace CentricDeviceMonitor.Models;

public sealed class NetworkQualityTestResult
{
    public string Target { get; init; } = string.Empty;
    public int PacketsSent { get; init; }
    public int PacketsReceived { get; init; }
    public int PacketsLost => Math.Max(0, PacketsSent - PacketsReceived);
    public double PacketLossPercent => PacketsSent == 0
        ? 0
        : PacketsLost * 100.0 / PacketsSent;
    public double? MinimumLatencyMs { get; init; }
    public double? AverageLatencyMs { get; init; }
    public double? MaximumLatencyMs { get; init; }
    public double? JitterMs { get; init; }
    public string Quality { get; init; } = "Not tested";
    public string Interpretation { get; init; } = string.Empty;
    public DateTime CompletedAt { get; init; } = DateTime.Now;
}

public sealed class NetworkQualityTestProgress
{
    public int PacketsSent { get; init; }
    public int PacketsReceived { get; init; }
    public int TotalPackets { get; init; }
    public double PacketLossPercent => PacketsSent == 0
        ? 0
        : Math.Max(0, PacketsSent - PacketsReceived) * 100.0 / PacketsSent;
    public double? LastLatencyMs { get; init; }
    public double? AverageLatencyMs { get; init; }
    public double? JitterMs { get; init; }
    public string LastStatus { get; init; } = "Waiting";
}
