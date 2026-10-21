using System.Windows;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor;

/// <summary>
/// Lighter refresh while nobody is looking: when the dashboard is minimised or closed to the notification area and no
/// desktop widget needs live data, the 3-second hardware refresh (sensor reads, tiles, graphs) pauses. A 15-second
/// tick keeps the CPU temperature alert working, using the background service's reading when it has one. Network PCs
/// alerts keep their own schedule. Everything refreshes as soon as the window is shown again.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan VisibleRefreshInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HiddenRefreshInterval = TimeSpan.FromSeconds(15);
    private bool _lightRefresh;

    private void UpdateRefreshMode()
    {
        bool hidden = !IsVisible || WindowState == WindowState.Minimized;
        bool light = hidden && _desktopWidgets.Count == 0;
        if (light == _lightRefresh)
        {
            return;
        }

        _lightRefresh = light;
        _hardwareTimer.Interval = light ? HiddenRefreshInterval : VisibleRefreshInterval;
        if (!light)
        {
            // Back on screen: bring every tile up to date straight away instead of waiting for the next tick.
            _ = RefreshAfterHiddenAsync();
        }
    }

    private async Task RefreshAfterHiddenAsync()
    {
        await RefreshCpuPackagePowerAsync();
        await RefreshGpuPowerAsync();
        await RefreshCpuTemperatureAsync();
        RefreshSystemResourceUsage();
        RefreshActivityTiles();
    }

    /// <summary>Only what alerts need: the CPU temperature, from the service when it has a CPU-specific reading.</summary>
    private async Task RefreshWhileHiddenAsync()
    {
        BackgroundServiceSnapshot? snapshot = _backgroundServiceSnapshot;
        if (IsBackgroundServiceRunning && snapshot is { CpuIsAvailable: true, CpuIsCpuSpecific: true, CpuTemperatureCelsius: double temperature }
            && DateTime.Now - snapshot.UpdatedAt <= TimeSpan.FromSeconds(30))
        {
            EvaluateTemperatureAlert(temperature, isCpuSpecific: true);
            return;
        }

        // No service reading: the full CPU path reads it locally (and also logs overheating as before).
        await RefreshCpuTemperatureAsync();
    }
}
