using System.Text.Json.Serialization;

namespace CentricDeviceMonitor.Models;

// Kept under the historical type name so existing project references remain simple.
// Version 2 entries represent connection-loss incidents, not individual ping checks.
// Version 3 adds a human-readable shutdown/restart reachability event classification.
public sealed class DevicePingLogEntry
{
    public int SchemaVersion { get; set; }

    public DateTime LostAt { get; set; }

    public DateTime? RecoveredAt { get; set; }

    public double? ActiveDurationBeforeLossSeconds { get; set; }

    public string ErrorMessage { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string EventMessage { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsIncident => SchemaVersion >= 2 && LostAt != default;

    [JsonIgnore]
    public bool IsActive => IsIncident && !RecoveredAt.HasValue;

    [JsonIgnore]
    public string LostAtDisplay => LostAt == default ? "-" : LostAt.ToString("dd MMM yyyy HH:mm:ss");

    [JsonIgnore]
    public string RecoveredAtDisplay => RecoveredAt.HasValue
        ? RecoveredAt.Value.ToString("dd MMM yyyy HH:mm:ss")
        : "Still offline";

    [JsonIgnore]
    public string LostDurationDisplay => !IsIncident
        ? "-"
        : FormatDuration((RecoveredAt ?? DateTime.Now) - LostAt);

    [JsonIgnore]
    public string ActiveDurationDisplay => ActiveDurationBeforeLossSeconds.HasValue
        ? FormatDuration(TimeSpan.FromSeconds(Math.Max(0, ActiveDurationBeforeLossSeconds.Value)))
        : "Unknown";

    [JsonIgnore]
    public string StatusDisplay => IsActive ? "Offline" : "Recovered";

    [JsonIgnore]
    public string EventDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(EventMessage))
            {
                return EventMessage;
            }

            return IsActive
                ? $"Device became unreachable at {LostAtDisplay}. Shutdown/restart or a network interruption is possible."
                : $"Connection was unavailable from {LostAtDisplay} until {RecoveredAtDisplay}.";
        }
    }

    [JsonIgnore]
    public string EventTypeDisplay => EventType switch
    {
        "ShutdownRestartInferred" => "Shutdown / restart",
        "ReachabilityLoss" => "Unreachable",
        _ => "Connection loss"
    };

    [JsonIgnore]
    public string ErrorDisplay => string.IsNullOrWhiteSpace(ErrorMessage) ? "Ping timeout / no response" : ErrorMessage;

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}"
            : duration.ToString(@"hh\:mm\:ss");
    }
}
