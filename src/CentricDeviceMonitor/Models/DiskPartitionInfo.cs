namespace CentricDeviceMonitor.Models;

public sealed class DiskPartitionInfo
{
    public int DiskNumber { get; init; }

    public int PartitionNumber { get; init; }

    public char? DriveLetter { get; init; }

    public ulong SizeBytes { get; init; }

    public string Type { get; init; } = "Unknown";

    public bool IsBoot { get; init; }

    public bool IsSystem { get; init; }

    public bool IsActive { get; init; }

    public bool IsHidden { get; init; }

    public bool IsReadOnly { get; init; }

    public string DriveLetterText => DriveLetter.HasValue ? $"{DriveLetter}:" : "-";

    public string SizeText => FormatBytes(SizeBytes);

    public string FlagsText
    {
        get
        {
            List<string> flags = new();
            if (IsBoot) flags.Add("Boot");
            if (IsSystem) flags.Add("System");
            if (IsActive) flags.Add("Active");
            if (IsHidden) flags.Add("Hidden");
            if (IsReadOnly) flags.Add("Read-only");
            return flags.Count == 0 ? "Data" : string.Join(", ", flags);
        }
    }

    public bool IsProtected => IsBoot || IsSystem;

    public string DisplayText => $"Partition {PartitionNumber} ({DriveLetterText}) - {SizeText} - {Type}";

    private static string FormatBytes(ulong bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        return bytes >= gb ? $"{bytes / gb:0.0} GB" : $"{bytes / (1024d * 1024d):0.0} MB";
    }
}
