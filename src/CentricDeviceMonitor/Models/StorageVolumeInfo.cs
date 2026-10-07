namespace CentricDeviceMonitor.Models;

public sealed class StorageVolumeInfo
{
    public string RootPath { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string DriveType { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }

    public string DisplayName
    {
        get
        {
            string name = string.IsNullOrWhiteSpace(Label) ? RootPath : $"{RootPath}  {Label}";
            string type = string.IsNullOrWhiteSpace(DriveType) ? string.Empty : $" [{DriveType}]";
            return $"{name}{type}  ({FormatBytes(FreeBytes)} free)";
        }
    }

    private static string FormatBytes(long bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        const double tb = gb * 1024d;
        return bytes >= tb ? $"{bytes / tb:0.00} TB" : $"{bytes / gb:0.0} GB";
    }
}
