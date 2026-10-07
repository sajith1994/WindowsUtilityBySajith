namespace CentricDeviceMonitor.Models;

public sealed class StorageCapacityProgress
{
    public string Phase { get; set; } = string.Empty;
    public long ProcessedBytes { get; set; }
    public long TotalBytes { get; set; }
    public int FileIndex { get; set; }

    public long RequestedBytes { get; set; }
    public long WrittenBytes { get; set; }
    public long VerifiedBytes { get; set; }

    // Step 1 - write/fill metrics.
    public double WriteCurrentMegabytesPerSecond { get; set; }
    public double WriteAverageMegabytesPerSecond { get; set; }
    public double WriteFastestMegabytesPerSecond { get; set; }
    public TimeSpan WriteElapsed { get; set; }
    public TimeSpan? WriteEstimatedRemaining { get; set; }

    // Step 2 - read/verification metrics.
    public double VerifyCurrentMegabytesPerSecond { get; set; }
    public double VerifyAverageMegabytesPerSecond { get; set; }
    public double VerifyFastestMegabytesPerSecond { get; set; }
    public TimeSpan VerifyElapsed { get; set; }
    public TimeSpan? VerifyEstimatedRemaining { get; set; }

    public TimeSpan TotalElapsed { get; set; }

    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp(ProcessedBytes / (double)TotalBytes, 0, 1);
    public double WriteFraction => RequestedBytes <= 0 ? 0 : Math.Clamp(WrittenBytes / (double)RequestedBytes, 0, 1);
    public double VerifyFraction => RequestedBytes <= 0 ? 0 : Math.Clamp(VerifiedBytes / (double)RequestedBytes, 0, 1);
    public bool IsWritePhase => Phase.StartsWith("Writing", StringComparison.OrdinalIgnoreCase);
    public bool IsVerifyPhase => Phase.StartsWith("Verifying", StringComparison.OrdinalIgnoreCase);
}
