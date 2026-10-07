namespace CentricDeviceMonitor.Models;

public sealed class StorageBenchmarkResult
{
    public DateTime RecordedAt { get; set; }
    public string Volume { get; set; } = string.Empty;
    public int TestSizeMegabytes { get; set; }
    public double SequentialWriteMegabytesPerSecond { get; set; }
    public double SequentialReadMegabytesPerSecond { get; set; }

    /// <summary>
    /// True when the run used FILE_FLAG_NO_BUFFERING and the figures therefore reflect the device.
    /// When false the volume rejected unbuffered I/O and Windows served part or all of the read
    /// from RAM, so the read figure is not a drive measurement and must not be presented as one.
    /// </summary>
    public bool CacheBypassed { get; set; }

    /// <summary>Transfer profile used, e.g. "SEQ1M Q8T1". Recorded so old rows stay interpretable.</summary>
    public string Profile { get; set; } = string.Empty;
}
