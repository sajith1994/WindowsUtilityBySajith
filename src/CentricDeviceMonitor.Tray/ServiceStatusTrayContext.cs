using System.Diagnostics;
using System.Drawing;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows.Forms;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.TrayHost;

internal sealed class ServiceStatusTrayContext : ApplicationContext
{
    private const string ServiceName = "CentricDeviceMonitorService";
    private static readonly TimeSpan FreshHeartbeatWindow = TimeSpan.FromSeconds(15);

    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly ToolStripMenuItem _serviceStatusItem;
    private readonly ToolStripMenuItem _monitoringStatusItem;
    private readonly ToolStripMenuItem _heartbeatStatusItem;
    private readonly Icon _baseIcon;
    private Icon? _renderedStatusIcon;

    private string _serviceStatus = "Checking";
    private string _monitoringStatus = "Checking";
    private string _heartbeatStatus = "Waiting for heartbeat";
    private bool _lastRunningState;
    private bool _firstRefresh = true;

    public ServiceStatusTrayContext()
    {
        _baseIcon = LoadApplicationIcon();

        _serviceStatusItem = new ToolStripMenuItem("Service: Checking...") { Enabled = false };
        _monitoringStatusItem = new ToolStripMenuItem("Monitoring: Checking...") { Enabled = false };
        _heartbeatStatusItem = new ToolStripMenuItem("Heartbeat: Checking...") { Enabled = false };

        ContextMenuStrip menu = new();
        menu.Items.Add(_serviceStatusItem);
        menu.Items.Add(_monitoringStatusItem);
        menu.Items.Add(_heartbeatStatusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh status", null, (_, _) => RefreshStatus(showBalloon: true));
        menu.Items.Add("Open Windows Utility", null, (_, _) => OpenDashboard());
        menu.Items.Add("Open service log", null, (_, _) => OpenServiceLog());
        menu.Items.Add("Open Windows Services", null, (_, _) => OpenWindowsServices());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit status icon", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => RefreshStatus(showBalloon: false);

        _notifyIcon = new NotifyIcon
        {
            Text = "Windows Utility - checking service",
            Icon = _baseIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) =>
        {
            RefreshStatus(showBalloon: false);
            ShowStatusBalloon();
        };

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _refreshTimer.Tick += (_, _) => RefreshStatus(showBalloon: false);
        _refreshTimer.Start();

        RefreshStatus(showBalloon: true);
    }

    private void RefreshStatus(bool showBalloon)
    {
        try
        {
            // The main dashboard already owns a notification-area icon. Hide this
            // lightweight companion while the dashboard is running to avoid two icons.
            bool dashboardTrayAvailable = IsDashboardTrayAvailableInCurrentSession();
            _notifyIcon.Visible = !dashboardTrayAvailable;

            ServiceControllerStatus? status = QueryServiceStatus();
            bool running = status == ServiceControllerStatus.Running;
            _serviceStatus = status switch
            {
                ServiceControllerStatus.Running => "Running",
                ServiceControllerStatus.StartPending => "Starting",
                ServiceControllerStatus.StopPending => "Stopping",
                ServiceControllerStatus.Stopped => "Stopped",
                ServiceControllerStatus.Paused => "Paused",
                ServiceControllerStatus.PausePending => "Pausing",
                ServiceControllerStatus.ContinuePending => "Continuing",
                null => "Not installed",
                _ => status.ToString() ?? "Unknown"
            };

            BackgroundServiceSnapshot? snapshot = LoadSnapshot();
            bool heartbeatFresh = snapshot is not null &&
                snapshot.UpdatedAt != default &&
                DateTime.Now - snapshot.UpdatedAt <= FreshHeartbeatWindow;

            if (!running)
            {
                _monitoringStatus = "Unavailable";
            }
            else if (!heartbeatFresh)
            {
                _monitoringStatus = "Service running; heartbeat stale";
            }
            else if (snapshot!.MonitoringEnabled)
            {
                _monitoringStatus = $"Active - {snapshot.DeviceCount} managed device(s)";
            }
            else
            {
                _monitoringStatus = "Service running; ping monitoring paused";
            }

            _heartbeatStatus = snapshot is null || snapshot.UpdatedAt == default
                ? "No service heartbeat yet"
                : heartbeatFresh
                    ? $"Updated {FormatAge(DateTime.Now - snapshot.UpdatedAt)} ago"
                    : $"Last update {snapshot.UpdatedAt:dd MMM yyyy HH:mm:ss}";

            _serviceStatusItem.Text = $"Service: {_serviceStatus}";
            _monitoringStatusItem.Text = $"Monitoring: {_monitoringStatus}";
            _heartbeatStatusItem.Text = $"Heartbeat: {_heartbeatStatus}";
            _notifyIcon.Text = LimitTooltip($"Windows Utility | Service {_serviceStatus} | {_monitoringStatus}");

            UpdateStatusIcon(running, heartbeatFresh);

            bool stateChanged = !_firstRefresh && running != _lastRunningState;
            _lastRunningState = running;
            _firstRefresh = false;

            if (showBalloon || stateChanged)
            {
                ShowStatusBalloon();
            }
        }
        catch
        {
            _serviceStatus = "Status unavailable";
            _monitoringStatus = "Unable to query service";
            _heartbeatStatus = "Unknown";
            _serviceStatusItem.Text = $"Service: {_serviceStatus}";
            _monitoringStatusItem.Text = $"Monitoring: {_monitoringStatus}";
            _heartbeatStatusItem.Text = $"Heartbeat: {_heartbeatStatus}";
            _notifyIcon.Text = "Windows Utility - service status unavailable";
            UpdateStatusIcon(running: false, heartbeatFresh: false);
        }
    }

    private static ServiceControllerStatus? QueryServiceStatus()
    {
        try
        {
            using ServiceController controller = new(ServiceName);
            return controller.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static BackgroundServiceSnapshot? LoadSnapshot()
    {
        try
        {
            if (!File.Exists(SharedDataPaths.ServiceSnapshotFilePath))
            {
                return null;
            }

            using FileStream stream = new(
                SharedDataPaths.ServiceSnapshotFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return JsonSerializer.Deserialize<BackgroundServiceSnapshot>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    private void ShowStatusBalloon()
    {
        if (!_notifyIcon.Visible)
        {
            return;
        }

        ToolTipIcon icon = _lastRunningState ? ToolTipIcon.Info : ToolTipIcon.Warning;
        _notifyIcon.ShowBalloonTip(
            4500,
            "Windows Utility background monitoring",
            $"Service: {_serviceStatus}\nMonitoring: {_monitoringStatus}\nHeartbeat: {_heartbeatStatus}",
            icon);
    }

    private void UpdateStatusIcon(bool running, bool heartbeatFresh)
    {
        Color statusColor = running && heartbeatFresh
            ? Color.FromArgb(52, 199, 89)
            : running
                ? Color.FromArgb(255, 159, 10)
                : Color.FromArgb(255, 59, 48);

        Icon newIcon = CreateStatusOverlayIcon(_baseIcon, statusColor);
        Icon? previous = _renderedStatusIcon;
        _renderedStatusIcon = newIcon;
        _notifyIcon.Icon = newIcon;
        previous?.Dispose();
    }

    private static Icon CreateStatusOverlayIcon(Icon baseIcon, Color statusColor)
    {
        using Bitmap bitmap = new(32, 32);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.DrawIcon(baseIcon, new Rectangle(0, 0, 32, 32));

        using SolidBrush outline = new(Color.White);
        using SolidBrush fill = new(statusColor);
        graphics.FillEllipse(outline, 19, 19, 13, 13);
        graphics.FillEllipse(fill, 21, 21, 9, 9);

        IntPtr iconHandle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(iconHandle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                Icon? extracted = Icon.ExtractAssociatedIcon(processPath);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch
        {
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static bool IsDashboardTrayAvailableInCurrentSession()
    {
        try
        {
            string path = SharedDataPaths.DashboardTrayReadyFilePath;
            if (!File.Exists(path))
            {
                return false;
            }

            string[] parts = File.ReadAllText(path).Trim().Split('|');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out int pid) ||
                !int.TryParse(parts[1], out int sessionId) ||
                sessionId != Process.GetCurrentProcess().SessionId)
            {
                return false;
            }

            using Process process = Process.GetProcessById(pid);
            bool valid = string.Equals(process.ProcessName, "CentricDeviceMonitor", StringComparison.OrdinalIgnoreCase);
            if (!valid)
            {
                TryDeleteStaleDashboardTrayMarker(path);
            }

            return valid;
        }
        catch
        {
            TryDeleteStaleDashboardTrayMarker(SharedDataPaths.DashboardTrayReadyFilePath);
            return false;
        }
    }

    private static void TryDeleteStaleDashboardTrayMarker(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void OpenDashboard()
    {
        string dashboardPath = Path.Combine(AppContext.BaseDirectory, "WindowsUtilityBySajith.exe");
        if (!File.Exists(dashboardPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(dashboardPath)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch
        {
        }
    }

    private static void OpenServiceLog()
    {
        try
        {
            SharedDataPaths.EnsureDirectories();
            if (!File.Exists(SharedDataPaths.ServiceLogFilePath))
            {
                File.WriteAllText(
                    SharedDataPaths.ServiceLogFilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] No service log entries have been recorded yet.{Environment.NewLine}");
            }

            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{SharedDataPaths.ServiceLogFilePath}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private static void OpenWindowsServices()
    {
        try
        {
            Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalSeconds < 60)
        {
            return $"{Math.Max(0, (int)age.TotalSeconds)}s";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes}m";
        }

        return $"{(int)age.TotalHours}h";
    }

    private static string LimitTooltip(string value) =>
        value.Length <= 63 ? value : value[..60] + "...";

    protected override void ExitThreadCore()
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _renderedStatusIcon?.Dispose();
        _baseIcon.Dispose();
        base.ExitThreadCore();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
