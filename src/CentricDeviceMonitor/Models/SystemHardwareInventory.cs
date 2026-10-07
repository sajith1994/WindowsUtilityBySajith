namespace CentricDeviceMonitor.Models;

public sealed record DisplayInventoryInfo(
    string Name,
    string Resolution,
    string RefreshRate,
    string PhysicalSize,
    bool IsPrimary)
{
    public string Display => $"{Name}{(IsPrimary ? " (Primary)" : string.Empty)} • {PhysicalSize} • {Resolution} @ {RefreshRate}";
}

public sealed record GraphicsAdapterInventoryInfo(
    string Name,
    string MemoryDisplay,
    string DriverVersion,
    string VideoProcessor,
    string Status)
{
    public string Display => $"{Name} • {MemoryDisplay} • Driver {DriverVersion}";
}

public sealed record MemoryModuleInventoryInfo(
    string Location,
    string Manufacturer,
    string PartNumber,
    ulong CapacityBytes,
    uint SpeedMt)
{
    public string Display => $"{Location} • {SystemHardwareInventoryServiceFormatting.FormatBytes(CapacityBytes)} • {(SpeedMt > 0 ? $"{SpeedMt:0} MT/s" : "speed not reported")} • {Manufacturer} {PartNumber}".Trim();
}

public sealed record PhysicalDiskInventoryInfo(
    int Index,
    string Model,
    string InterfaceType,
    string MediaType,
    ulong SizeBytes)
{
    public string Display => $"Disk {Index} • {Model} • {SystemHardwareInventoryServiceFormatting.FormatBytes(SizeBytes)} • {InterfaceType} • {MediaType}";
}

public sealed record StorageVolumeInventoryInfo(
    string DriveLetter,
    string Label,
    string FileSystem,
    ulong CapacityBytes,
    ulong FreeBytes,
    string DriveType)
{
    public ulong UsedBytes => CapacityBytes >= FreeBytes ? CapacityBytes - FreeBytes : 0;

    public double UsedPercent => CapacityBytes == 0 ? 0 : UsedBytes * 100d / CapacityBytes;

    public string Display =>
        $"{DriveLetter} {Label} • {SystemHardwareInventoryServiceFormatting.FormatBytes(UsedBytes)} used / {SystemHardwareInventoryServiceFormatting.FormatBytes(CapacityBytes)} • {UsedPercent:0}% • {FileSystem} • {DriveType}".Trim();
}

public sealed class SystemHardwareInventory
{
    public string ComputerName { get; init; } = Environment.MachineName;

    public string Manufacturer { get; init; } = "Not reported";

    public string Model { get; init; } = "Not reported";

    public string SystemSku { get; init; } = "Not reported";

    public string SerialNumber { get; init; } = "Not reported";

    public int MemorySlots { get; init; }

    public IReadOnlyList<MemoryModuleInventoryInfo> MemoryModules { get; init; } = Array.Empty<MemoryModuleInventoryInfo>();

    public IReadOnlyList<DisplayInventoryInfo> Displays { get; init; } = Array.Empty<DisplayInventoryInfo>();

    public IReadOnlyList<GraphicsAdapterInventoryInfo> GraphicsAdapters { get; init; } = Array.Empty<GraphicsAdapterInventoryInfo>();

    public IReadOnlyList<PhysicalDiskInventoryInfo> PhysicalDisks { get; init; } = Array.Empty<PhysicalDiskInventoryInfo>();

    public IReadOnlyList<StorageVolumeInventoryInfo> Volumes { get; init; } = Array.Empty<StorageVolumeInventoryInfo>();

    public DateTime UpdatedAt { get; init; } = DateTime.Now;
}

internal static class SystemHardwareInventoryServiceFormatting
{
    public static string FormatBytes(ulong bytes)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}
