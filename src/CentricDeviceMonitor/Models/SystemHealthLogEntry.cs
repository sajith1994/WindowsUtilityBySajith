using System.Text.Json.Serialization;

namespace CentricDeviceMonitor.Models;

public sealed class SystemHealthLogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string EventType { get; set; } = string.Empty;

    public string Component { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public double? StartValue { get; set; }

    public double? PeakValue { get; set; }

    public string Unit { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsActive => !EndedAt.HasValue;

    [JsonIgnore]
    public string Status => IsActive ? "Active" : "Completed";

    [JsonIgnore]
    public string StartedAtDisplay => StartedAt.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string EndedAtDisplay => EndedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Active";

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

    [JsonIgnore]
    public string ValueDisplay
    {
        get
        {
            if (!PeakValue.HasValue)
            {
                return "-";
            }

            return string.IsNullOrWhiteSpace(Unit)
                ? $"{PeakValue.Value:0.0}"
                : $"{PeakValue.Value:0.0} {Unit}";
        }
    }
}
