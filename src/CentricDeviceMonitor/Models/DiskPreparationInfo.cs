namespace CentricDeviceMonitor.Models;

public sealed class DiskPreparationInfo
{
    public int Number { get; init; }

    public string FriendlyName { get; init; } = "Unknown disk";

    public string SerialNumber { get; init; } = string.Empty;

    public ulong SizeBytes { get; init; }

    public ulong AllocatedSizeBytes { get; init; }

    public ulong LargestFreeExtentBytes { get; init; }

    public int NumberOfPartitions { get; init; }

    public string PartitionStyle { get; init; } = "Unknown";

    public string BusType { get; init; } = "Unknown";

    public string HealthStatus { get; init; } = "Unknown";

    public bool IsBoot { get; init; }

    public bool IsSystem { get; init; }

    public bool IsOffline { get; init; }

    public bool IsReadOnly { get; init; }

    public ulong UnallocatedBytes => SizeBytes > AllocatedSizeBytes ? SizeBytes - AllocatedSizeBytes : 0UL;

    public string SizeText => FormatBytes(SizeBytes);

    public string AllocatedText => FormatBytes(AllocatedSizeBytes);

    public string UnallocatedText => FormatBytes(UnallocatedBytes);

    public string LargestFreeExtentText => FormatBytes(LargestFreeExtentBytes);

    public bool IsProtected => IsBoot || IsSystem;

    public string ProtectionText => IsProtected ? "Protected system disk" : "Data / removable disk";

    public bool CanInitialize => !IsProtected && string.Equals(PartitionStyle, "RAW", StringComparison.OrdinalIgnoreCase);

    public bool CanClean => !IsProtected && !string.Equals(PartitionStyle, "RAW", StringComparison.OrdinalIgnoreCase);

    public bool CanCreatePartition => !IsProtected &&
        (string.Equals(PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(PartitionStyle, "MBR", StringComparison.OrdinalIgnoreCase)) &&
        LargestFreeExtentBytes >= 8UL * 1024UL * 1024UL;

    public string DisplayText => $"Disk {Number} - {FriendlyName} - {SizeText} - {PartitionStyle}";

    private static string FormatBytes(ulong bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        return bytes >= gb ? $"{bytes / gb:0.0} GB" : $"{bytes / (1024d * 1024d):0.0} MB";
    }
}
