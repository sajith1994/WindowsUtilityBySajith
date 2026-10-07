namespace CentricDeviceMonitor.Models;

public sealed class BatteryHealthInfo
{
    public string InstanceName { get; set; } = string.Empty;
    public string Model { get; set; } = "Not reported";
    public string Manufacturer { get; set; } = "Not reported";
    public string SerialNumber { get; set; } = "Not reported";
    public string UniqueId { get; set; } = "Not reported";
    public string Chemistry { get; set; } = "Not reported";
    public double? DesignedCapacityMWh { get; set; }
    public double? FullChargeCapacityMWh { get; set; }
    public double? RemainingCapacityMWh { get; set; }
    public double? HealthPercent { get; set; }
    public uint? CycleCount { get; set; }
    public int? EstimatedChargeRemainingPercent { get; set; }
    public int? EstimatedRunTimeMinutes { get; set; }
    public double? VoltageMillivolts { get; set; }
    public double? RateMilliwatts { get; set; }
    public bool? Charging { get; set; }
    public bool? Discharging { get; set; }
    public bool? PowerOnline { get; set; }
    public bool? Critical { get; set; }
    public string Status { get; set; } = "Unknown";
    public string Source { get; set; } = "Windows battery telemetry";

    public string DesignedCapacityText => FormatEnergy(DesignedCapacityMWh);
    public string FullChargeCapacityText => FormatEnergy(FullChargeCapacityMWh);
    public string RemainingCapacityText => FormatEnergy(RemainingCapacityMWh);
    public string HealthText => HealthPercent.HasValue ? $"{Math.Clamp(HealthPercent.Value, 0, 999):0.0}%" : "Not reported";
    public string CycleCountText => CycleCount.HasValue ? CycleCount.Value.ToString("N0") : "Not reported";
    public string ChargeRemainingText => EstimatedChargeRemainingPercent.HasValue ? $"{Math.Clamp(EstimatedChargeRemainingPercent.Value, 0, 100)}%" : "Not reported";
    public string RemainingRuntimeText => FormatRuntime(EstimatedRunTimeMinutes);
    public string VoltageText => VoltageMillivolts.HasValue && VoltageMillivolts.Value > 0 ? $"{VoltageMillivolts.Value / 1000d:0.00} V" : "Not reported";
    public string RateText => RateMilliwatts.HasValue ? $"{RateMilliwatts.Value / 1000d:+0.0;-0.0;0.0} W" : "Not reported";
    public string StateText
    {
        get
        {
            if (Charging == true) return "Charging";
            if (Discharging == true) return "Discharging";
            if (PowerOnline == true) return "Plugged in";
            return string.IsNullOrWhiteSpace(Status) ? "Unknown" : Status;
        }
    }

    private static string FormatEnergy(double? value)
    {
        if (!value.HasValue || value.Value <= 0)
        {
            return "Not reported";
        }

        return value.Value >= 1000
            ? $"{value.Value / 1000d:0.00} Wh"
            : $"{value.Value:0} mWh";
    }

    private static string FormatRuntime(int? minutes)
    {
        if (!minutes.HasValue || minutes.Value < 0 || minutes.Value >= 1_000_000)
        {
            return "Not available";
        }

        TimeSpan runtime = TimeSpan.FromMinutes(minutes.Value);
        if (runtime.TotalHours >= 24)
        {
            return $"{(int)runtime.TotalDays}d {runtime.Hours}h {runtime.Minutes}m";
        }

        if (runtime.TotalHours >= 1)
        {
            return $"{(int)runtime.TotalHours}h {runtime.Minutes}m";
        }

        return $"{Math.Max(0, runtime.Minutes)} min";
    }
}
