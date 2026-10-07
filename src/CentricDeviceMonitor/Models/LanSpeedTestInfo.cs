namespace CentricDeviceMonitor.Models;

public sealed class LanThroughputResult
{
    public string ServerHost { get; init; } = string.Empty;
    public int Port { get; init; }
    public int DurationSeconds { get; init; }
    public int ParallelStreams { get; init; }
    public double? LatencyMs { get; init; }
    public double UploadMbps { get; init; }
    public double DownloadMbps { get; init; }
    public DateTime CompletedAt { get; init; } = DateTime.Now;
}

public sealed class LanSpeedTestProgress
{
    public string Phase { get; init; } = "Ready";
    public string Detail { get; init; } = string.Empty;
    public double? LatencyMs { get; init; }
    public double? UploadMbps { get; init; }
    public double? DownloadMbps { get; init; }
    public double? CurrentMbps { get; init; }
    public double? ProgressPercent { get; init; }
}
