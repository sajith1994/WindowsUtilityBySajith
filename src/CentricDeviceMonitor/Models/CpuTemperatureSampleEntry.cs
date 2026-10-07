using System.Text.Json.Serialization;

namespace CentricDeviceMonitor.Models;

public sealed class CpuTemperatureSampleEntry
{
    public DateTime RecordedAt { get; set; }

    public double TemperatureCelsius { get; set; }

    public string SensorName { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    [JsonIgnore]
    public string RecordedAtDisplay => RecordedAt.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string TemperatureDisplay => $"{TemperatureCelsius:0.0} °C";
}
