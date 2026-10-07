namespace CentricDeviceMonitor.Models;

public sealed class StorageCapacityVerificationResult
{
    public DateTime RecordedAt { get; set; }
    public string Volume { get; set; } = string.Empty;
    public long RequestedBytes { get; set; }
    public long WrittenBytes { get; set; }
    public long VerifiedBytes { get; set; }
    public long FreeBytesAtStart { get; set; }
    public long SafetyReserveBytes { get; set; }
    public bool Passed { get; set; }
    public long? FailedBlockIndex { get; set; }
    public string FailureReason { get; set; } = string.Empty;

    public string TestedText => FormatBytes(VerifiedBytes > 0 ? VerifiedBytes : WrittenBytes);
    public string ResultText => Passed ? $"Passed - {TestedText} verified" : $"Failed - {FailureReason}";

    public static string FormatBytes(long bytes)
    {
        const double mb = 1024d * 1024d;
        const double gb = mb * 1024d;
        const double tb = gb * 1024d;
        if (bytes >= tb) return $"{bytes / tb:0.00} TB";
        if (bytes >= gb) return $"{bytes / gb:0.00} GB";
        return $"{bytes / mb:0.0} MB";
    }
}
