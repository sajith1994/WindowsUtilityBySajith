namespace CentricDeviceMonitor.Models;

public sealed class StorageDriveHealth
{
    public string DeviceId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = "Unknown disk";
    public string SerialNumber { get; set; } = string.Empty;
    public string MediaType { get; set; } = "Unspecified";
    public string BusType { get; set; } = "Unknown";
    public string HealthStatus { get; set; } = "Unknown";
    public string OperationalStatus { get; set; } = "Unknown";
    public ulong? SizeBytes { get; set; }
    public double? WearPercent { get; set; }
    public double? PowerOnHours { get; set; }
    public double? TemperatureCelsius { get; set; }
    public double? TemperatureMaxCelsius { get; set; }
    public ulong? ReadErrorsTotal { get; set; }
    public ulong? WriteErrorsTotal { get; set; }
    public string Source { get; set; } = "Windows Storage";

    public string SizeText => SizeBytes.HasValue ? FormatBytes(SizeBytes.Value) : "Not reported";
    public string WearText => WearPercent.HasValue ? $"{WearPercent.Value:0}% used" : "Not reported";
    public string RemainingLifeText => WearPercent.HasValue
        ? $"{Math.Clamp(100.0 - WearPercent.Value, 0.0, 100.0):0}% endurance remaining"
        : "Not reported";
    public string PowerOnHoursText => PowerOnHours.HasValue ? $"{PowerOnHours.Value:0} h" : "Not reported";
    public string TemperatureText => TemperatureCelsius.HasValue ? $"{TemperatureCelsius.Value:0} °C" : "Not reported";
    public string ErrorText => ReadErrorsTotal.HasValue || WriteErrorsTotal.HasValue
        ? $"R {ReadErrorsTotal.GetValueOrDefault():N0} / W {WriteErrorsTotal.GetValueOrDefault():N0}"
        : "Not reported";

    private static string FormatBytes(ulong bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        const double tb = gb * 1024d;
        return bytes >= tb ? $"{bytes / tb:0.00} TB" : $"{bytes / gb:0.0} GB";
    }
}
