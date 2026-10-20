using System.Management;
using System.Runtime.InteropServices;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.ServiceHost;

/// <summary>
/// PC model and battery details for the remote stats. The model is read once. Battery charge, state and time left
/// come from GetSystemPowerStatus on every request (instant); capacity, health and cycle count come from the same
/// battery diagnostics the dashboard uses (WMI plus powercfg), which are slower, so they are refreshed in the
/// background every 15 minutes.
/// </summary>
internal sealed class DeviceDetailsProvider
{
    private static readonly TimeSpan BatteryDetailsLifetime = TimeSpan.FromMinutes(15);
    private readonly Lazy<(string Manufacturer, string Model)> _model = new(ReadModel);
    private readonly object _lock = new();
    private IReadOnlyList<BatteryHealthInfo> _batteries = Array.Empty<BatteryHealthInfo>();
    private DateTime _batteriesReadAt = DateTime.MinValue;
    private bool _batteryRefreshRunning;

    public void Fill(RemoteStatsPayload payload)
    {
        (payload.SystemManufacturer, payload.SystemModel) = _model.Value;

        if (!GetSystemPowerStatus(out SystemPowerStatus power) || (power.BatteryFlag & 128) != 0 || power.BatteryFlag == 255)
        {
            payload.HasBattery = false;
            return;
        }

        payload.HasBattery = true;
        payload.BatteryPercent = power.BatteryLifePercent <= 100 ? power.BatteryLifePercent : null;
        bool charging = (power.BatteryFlag & 8) != 0;
        payload.BatteryState = charging ? "Charging" : power.ACLineStatus == 1 ? "Plugged in" : "On battery";
        payload.BatteryRuntimeMinutes = power.ACLineStatus != 1 && power.BatteryLifeTime is > 0 and < 7 * 24 * 3600
            ? (int)(power.BatteryLifeTime / 60)
            : null;

        IReadOnlyList<BatteryHealthInfo> batteries;
        lock (_lock)
        {
            batteries = _batteries;
            if (!_batteryRefreshRunning && DateTime.UtcNow - _batteriesReadAt > BatteryDetailsLifetime)
            {
                _batteryRefreshRunning = true;
                _ = Task.Run(RefreshBatteryDetailsAsync);
            }
        }

        double design = batteries.Sum(battery => battery.DesignedCapacityMWh ?? 0);
        double full = batteries.Sum(battery => battery.FullChargeCapacityMWh ?? 0);
        payload.BatteryDesignMWh = design > 0 ? design : null;
        payload.BatteryFullMWh = full > 0 ? full : null;
        payload.BatteryHealthPercent = design > 0 && full > 0 ? Math.Min(100, full / design * 100) : null;
        payload.BatteryCycleCount = batteries.Select(battery => battery.CycleCount).Where(cycles => cycles is > 0).Max();
    }

    private async Task RefreshBatteryDetailsAsync()
    {
        try
        {
            IReadOnlyList<BatteryHealthInfo> batteries = await new BatteryDiagnosticsService().GetBatteryHealthAsync();
            lock (_lock)
            {
                _batteries = batteries;
            }
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("Remote monitoring battery details", exception);
        }
        finally
        {
            lock (_lock)
            {
                _batteriesReadAt = DateTime.UtcNow;
                _batteryRefreshRunning = false;
            }
        }
    }

    /// <summary>Maker and model. Lenovo reports a machine type as the model; its friendly name is in the product version.</summary>
    private static (string Manufacturer, string Model) ReadModel()
    {
        string manufacturer = string.Empty;
        string model = string.Empty;
        try
        {
            using (ManagementObjectSearcher searcher = new("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            {
                foreach (ManagementBaseObject system in searcher.Get())
                {
                    using (system)
                    {
                        manufacturer = (system["Manufacturer"] as string ?? string.Empty).Trim();
                        model = (system["Model"] as string ?? string.Empty).Trim();
                    }
                }
            }

            using (ManagementObjectSearcher searcher = new("SELECT Version FROM Win32_ComputerSystemProduct"))
            {
                foreach (ManagementBaseObject product in searcher.Get())
                {
                    using (product)
                    {
                        string friendly = (product["Version"] as string ?? string.Empty).Trim();
                        if (manufacturer.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) && friendly.Length > 0
                            && !friendly.Equals("None", StringComparison.OrdinalIgnoreCase))
                        {
                            model = model.Length > 0 && !friendly.Contains(model, StringComparison.OrdinalIgnoreCase)
                                ? $"{friendly} ({model})"
                                : friendly;
                        }
                    }
                }
            }
        }
        catch (ManagementException exception)
        {
            ServiceLog.WriteException("Remote monitoring PC model", exception);
        }

        // Placeholder values some builders leave in the firmware.
        string[] placeholders = { "System Product Name", "To Be Filled By O.E.M.", "Default string", "System manufacturer" };
        if (placeholders.Any(placeholder => model.Equals(placeholder, StringComparison.OrdinalIgnoreCase)))
        {
            model = string.Empty;
        }

        if (placeholders.Any(placeholder => manufacturer.Equals(placeholder, StringComparison.OrdinalIgnoreCase)))
        {
            manufacturer = string.Empty;
        }

        return (manufacturer, model);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
