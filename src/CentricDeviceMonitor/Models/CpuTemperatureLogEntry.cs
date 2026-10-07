using System.Text.Json.Serialization;

namespace CentricDeviceMonitor.Models;

public sealed class CpuTemperatureLogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public double StartTemperatureCelsius { get; set; }

    public double PeakTemperatureCelsius { get; set; }

    public string SensorName { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsActive => !EndedAt.HasValue;

    [JsonIgnore]
    public string Status => IsActive ? "Active" : "Completed";

    [JsonIgnore]
    public string StartedAtDisplay => StartedAt.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string EndedAtDisplay => EndedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Still above 90 °C";

    [JsonIgnore]
    public string StartTemperatureDisplay => $"{StartTemperatureCelsius:0.0} °C";

    [JsonIgnore]
    public string PeakTemperatureDisplay => $"{PeakTemperatureCelsius:0.0} °C";

    [JsonIgnore]
    public string DurationDisplay
    {
        get
        {
            TimeSpan duration = (EndedAt ?? DateTime.Now) - StartedAt;
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            return duration.TotalDays >= 1
                ? $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}"
                : duration.ToString(@"hh\:mm\:ss");
        }
    }
}
