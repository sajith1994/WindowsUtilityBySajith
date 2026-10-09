namespace CentricDeviceMonitor.Models;

public sealed class AppSettings
{
    // UI theme: System follows the current Windows app theme; Light/Dark are explicit overrides.
    public string UiThemePreference { get; set; } = "System";

    public bool AutomaticMonitoringEnabled { get; set; } = true;

    /// <summary>Shows the notification-area icon for this dashboard process.</summary>
    public bool TrayIconEnabled { get; set; } = true;

    /// <summary>Allows balloon notifications from the dashboard. Sound alerts are separate.</summary>
    public bool DesktopNotificationsEnabled { get; set; } = true;

    /// <summary>Checks for a newer release shortly after the dashboard starts.</summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    /// <summary>Update version whose dashboard alert was snoozed. A newer release shows the alert again.</summary>
    public string UpdateSnoozedVersion { get; set; } = string.Empty;

    /// <summary>The snoozed update alert stays hidden until this time (UTC).</summary>
    public DateTime? UpdateSnoozedUntilUtc { get; set; }

    public int PingIntervalSeconds { get; set; } = 60;

    // The historical overheat event log still starts above 90 C.
    // This setting controls the user-facing audible alert threshold.
    public double CpuTemperatureAlertThresholdCelsius { get; set; } = 98.0;

    public bool CpuTemperatureSoundAlertEnabled { get; set; } = true;

    // Repeats the user-facing temperature warning while the CPU remains above the configured threshold.
    public int CpuTemperatureAlertIntervalSeconds { get; set; } = 60;

    // Legacy one-time delay setting retained so older settings files remain compatible.
    public int PowerActionDelayMinutes { get; set; } = 1;

    // Daily unattended Windows power schedule. Time is stored in local 24-hour clock values.
    public bool PowerScheduleEnabled { get; set; }

    public string PowerScheduleAction { get; set; } = "Restart";

    public int PowerScheduleHour { get; set; } = 23;

    public int PowerScheduleMinute { get; set; } = 30;

    // YYYY-MM-DD marker written before a scheduled action is initiated to prevent a reboot loop.
    public string PowerScheduleLastTriggeredDate { get; set; } = string.Empty;

    // When enabled, Windows closes applications with unsaved work during the power action.
    public bool ForcePowerAction { get; set; }
}

