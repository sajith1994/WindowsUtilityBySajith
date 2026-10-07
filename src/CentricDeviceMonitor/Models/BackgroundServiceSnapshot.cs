namespace CentricDeviceMonitor.Models;

public sealed class BackgroundServiceSnapshot
{
    public DateTime ServiceStartedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public bool MonitoringEnabled { get; set; }

    public int PingIntervalSeconds { get; set; } = 60;

    public DateTime? NextPingAt { get; set; }

    public bool PingCycleRunning { get; set; }

    public int DeviceCount { get; set; }

    public int OnlineDeviceCount { get; set; }

    public bool CpuIsAvailable { get; set; }

    public bool CpuIsCpuSpecific { get; set; }

    public double? CpuTemperatureCelsius { get; set; }

    public string CpuDisplay { get; set; } = "Not available";

    public string CpuSensorName { get; set; } = "Not available";

    public string CpuSource { get; set; } = "Background service";

    public string CpuDiagnostic { get; set; } = string.Empty;

    public double? CpuFanRpm { get; set; }

    public string CpuFanDisplay { get; set; } = "Fan: Not reported";

    public string CpuFanSensorName { get; set; } = string.Empty;

    public string CpuFanSource { get; set; } = string.Empty;

    public string CpuFanDiagnostic { get; set; } = string.Empty;

    public double? CpuPackagePowerWatts { get; set; }

    public string CpuPackagePowerDisplay { get; set; } = "Not available";

    public string CpuPackagePowerSensorName { get; set; } = string.Empty;

    public string CpuPackagePowerSource { get; set; } = string.Empty;

    public string CpuPackagePowerDiagnostic { get; set; } = string.Empty;

    public DateTime? CpuPackagePowerRecordedAt { get; set; }

    public double? GpuPowerWatts { get; set; }

    public string GpuPowerDisplay { get; set; } = "Not available";

    public string GpuPowerSensorName { get; set; } = string.Empty;

    public string GpuPowerSource { get; set; } = string.Empty;

    public string GpuPowerDiagnostic { get; set; } = string.Empty;

    public DateTime? GpuPowerRecordedAt { get; set; }

    public bool PowerScheduleEnabled { get; set; }

    public string PowerScheduleAction { get; set; } = "Restart";

    public string PowerScheduleTimeText { get; set; } = "23:30";

    public DateTime? NextPowerActionAt { get; set; }

    public bool ForcePowerAction { get; set; }

    public double? CpuUsagePercent { get; set; }

    public double RamUsagePercent { get; set; }

    public ulong TotalPhysicalMemoryBytes { get; set; }

    public ulong AvailablePhysicalMemoryBytes { get; set; }

    public bool NetworkConnected { get; set; }

    public string NetworkSummary { get; set; } = "Checking...";

    public string CpuModel { get; set; } = "CPU model unavailable";

    public string RamSpeedDisplay { get; set; } = "RAM speed unavailable";

    public double CpuTemperatureAlertThresholdCelsius { get; set; } = 98.0;

    public bool CpuTemperatureAlertActive { get; set; }

    public List<DeviceRuntimeStatus> Devices { get; set; } = new();
}

public sealed class DeviceRuntimeStatus
{
    public Guid DeviceId { get; set; }

    public string Status { get; set; } = "Not checked";

    public string Hostname { get; set; } = "Not available";

    public string MacAddress { get; set; } = "Not available";

    public long? RoundTripTime { get; set; }

    public DateTime? LastChecked { get; set; }

    public string LastError { get; set; } = string.Empty;
}
