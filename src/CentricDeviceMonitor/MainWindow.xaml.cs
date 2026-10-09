using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Globalization;
using System.Media;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CentricDeviceMonitor.Dialogs;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;
using WpfApplication = System.Windows.Application;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMessageBox = System.Windows.MessageBox;
using WpfRadioButton = System.Windows.Controls.RadioButton;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace CentricDeviceMonitor;

public partial class MainWindow : Window
{
    private const double CpuOverheatThresholdCelsius = 90.0;
    private const double CpuHighUsageThresholdPercent = 95.0;
    private const double RamHighUsageThresholdPercent = 90.0;

    private WpfBrush NormalCpuTemperatureBrush => GetThemeBrush("TextBrush", WpfBrushes.Black);
    private WpfBrush WarningCpuTemperatureBrush => GetThemeBrush("DangerBrush", WpfBrushes.Red);
    private WpfBrush PowerScheduleEnabledBrush => GetThemeBrush("SuccessBrush", WpfBrushes.ForestGreen);
    private WpfBrush PowerScheduleDisabledBrush => GetThemeBrush("DangerBrush", WpfBrushes.Red);

    private readonly DeviceStorageService _storageService = new();
    private readonly DeviceLogService _logService = new();
    private readonly CpuTemperatureLogService _cpuTemperatureLogService = new();
    private readonly SystemHealthLogService _systemHealthLogService = new();
    private readonly SystemResourceMonitorService _systemResourceMonitorService = new();
    private readonly SystemHealthDetailsService _systemHealthDetailsService = new();
    private readonly NetworkSpeedTestService _networkSpeedTestService = new();
    private readonly SystemHardwareInventoryService _systemHardwareInventoryService = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly NetworkDeviceService _networkService = new();
    private readonly PowerPlanService _powerPlanService = new();
    private readonly HardwareMonitorService _hardwareMonitorService = new();
    private readonly PcHealthService _pcHealthService = new();
    private FanInventoryReading _lastFanInventory = new(false, Array.Empty<FanSensorReading>(), "Unknown", "Not read yet.");
    private double _lastMemoryUsagePercent;
    private bool _lastNetworkConnected;
    private string _lastNetworkSummary = string.Empty;
    private readonly StorageDiagnosticsService _pcHealthStorageService = new();
    private readonly WindowsStartupService _startupService = new();
    private readonly WindowsUtilityService _windowsUtilityService = new();
    private readonly TemporaryFileCleanupService _temporaryFileCleanupService = new();
    private readonly UserApplicationTerminationService _userApplicationTerminationService = new();
    private readonly ExternalWindowsToolService _externalWindowsToolService = new();
    private readonly BackgroundMonitoringServiceManager _backgroundServiceManager = new();
    private readonly BackgroundServiceRuntimeStore _backgroundRuntimeStore = new();
    private readonly DispatcherTimer _hardwareTimer;
    private readonly DispatcherTimer _serviceTimer;
    private readonly DispatcherTimer _deviceMonitorTimer;
    private readonly DispatcherTimer _updateCheckTimer;
    private readonly PrinterService _printerService = new();
    private IReadOnlyList<InstalledPrinterInfo> _installedPrinters = Array.Empty<InstalledPrinterInfo>();
    private DateTime _installedPrintersReadAtUtc = DateTime.MinValue;
    private string _lastSpoolerStatus = string.Empty;
    private bool _refreshingPrinters;
    private bool _scanningNetworkPrinters;
    private UpdateCheckResult? _availableUpdate;
    private DispatcherTimer? _autoInstallTimer;
    private int _autoInstallSecondsLeft;
    private bool _installingUpdate;
    private readonly DispatcherTimer _pingCountdownTimer;
    private readonly List<CpuTemperatureLogEntry> _cpuTemperatureLogs = new();

    private AppSettings _appSettings = new();
    private CpuTemperatureLogEntry? _activeCpuTemperatureEvent;
    private bool _loadingSettings;
    private bool _refreshingSpoolerStatus;
    private bool _refreshingDevices;
    private bool _refreshingCpuTemperature;
    private DateTime? _nextAutomaticPingAt;
    private DeviceEntry? _deviceBeingEdited;
    private BackgroundMonitoringServiceInfo? _backgroundServiceInfo;
    private BackgroundServiceSnapshot? _backgroundServiceSnapshot;
    private bool _refreshingBackgroundService;
    private bool _refreshingNetworkManagedDevicesPage;
    private bool _refreshingSystemHealthDetails;
    private bool _refreshingPowerPlans;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayBaseIcon;
    private System.Drawing.Icon? _trayStatusIcon;
    private System.Windows.Forms.ToolStripMenuItem? _trayServiceStatusItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayMonitoringStatusItem;
    private System.Windows.Forms.ToolStripMenuItem? _trayHeartbeatStatusItem;
    private bool _allowApplicationExit;
    private bool _trayHintShown;
    private bool _temperatureAlertActive;
    private DateTime? _lastTemperatureAlertAt;
    private bool _appliedDarkTheme;
    private bool _refreshingCpuPackagePower;
    private long _cpuPackagePowerSessionSampleCount;
    private double _cpuPackagePowerSessionTotalWatts;
    private double _cpuPackagePowerSessionMaxWatts;
    private DateTime? _lastCpuPackagePowerRecordedAt;
    private bool _refreshingGpuPower;
    private long _gpuPowerSessionSampleCount;
    private double _gpuPowerSessionTotalWatts;
    private double _gpuPowerSessionMaxWatts;
    private DateTime? _lastGpuPowerRecordedAt;
    private bool _refreshingSystemHardwareInventory;

    public ObservableCollection<DeviceEntry> Devices { get; } = new();

    private DeviceEntry? SelectedDevice => DeviceGrid.SelectedItem as DeviceEntry;

    private bool IsBackgroundServiceRunning => _backgroundServiceInfo?.IsRunning == true;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        NavigationList.SelectedIndex = 0;
        PopulatePowerTimeSelectors();

        _hardwareTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hardwareTimer.Tick += async (_, _) =>
        {
            // Read package power before other LibreHardwareMonitor calls so the power
            // sensor uses the full interval since the previous hardware refresh.
            await RefreshCpuPackagePowerAsync();
            await RefreshGpuPowerAsync();
            await RefreshCpuTemperatureAsync();
            RefreshSystemResourceUsage();
        };

        _serviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _serviceTimer.Tick += async (_, _) =>
        {
            await RefreshSpoolerStatusAsync();
            await RefreshBackgroundMonitoringServiceAsync();
            await RefreshSystemHealthDetailsAsync();
            RefreshSystemThemeIfNeeded();
        };

        // The dashboard usually stays running in the tray, so re-check for releases periodically.
        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateCheckTimer.Tick += async (_, _) => await CheckForUpdateInBackgroundAsync(TimeSpan.Zero);

        _deviceMonitorTimer = new DispatcherTimer();
        _deviceMonitorTimer.Tick += DeviceMonitorTimer_Tick;

        _pingCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pingCountdownTimer.Tick += (_, _) =>
        {
            UpdatePingCountdownDisplay();
            UpdatePowerScheduleStatusDisplay();
        };

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        InitializeTrayIcon();
        WpfApplication.Current.SessionEnding += (_, _) => _allowApplicationExit = true;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveWindowSizing();
        try
        {
            SharedDataPaths.MigrateLegacyCurrentUserData();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Shared data migration", exception);
        }

        _appSettings = await _settingsService.LoadAsync();
        await ReloadCpuTemperatureLogsAsync();

        _appSettings.PingIntervalSeconds = NormalizePingInterval(_appSettings.PingIntervalSeconds);
        _appSettings.CpuTemperatureAlertThresholdCelsius = NormalizeCpuAlertThreshold(_appSettings.CpuTemperatureAlertThresholdCelsius);
        _appSettings.CpuTemperatureAlertIntervalSeconds = NormalizeCpuAlertInterval(_appSettings.CpuTemperatureAlertIntervalSeconds);

        _loadingSettings = true;
        TrayIconToggle.IsChecked = _appSettings.TrayIconEnabled;
        DesktopNotificationsToggle.IsChecked = _appSettings.DesktopNotificationsEnabled;
        ApplyTrayIconVisibility();
        _appSettings.UiThemePreference = ThemeManager.NormalizePreference(_appSettings.UiThemePreference);
        _appliedDarkTheme = ThemeManager.Apply(_appSettings.UiThemePreference);
        SelectThemePreference(_appSettings.UiThemePreference);
        StartupCheckBox.IsChecked = _startupService.IsEnabled();
        AutomaticMonitoringCheckBox.IsChecked = _appSettings.AutomaticMonitoringEnabled;
        SelectPingIntervalItem(_appSettings.PingIntervalSeconds);
        CpuTemperatureAlertSlider.Value = _appSettings.CpuTemperatureAlertThresholdCelsius;
        CpuTemperatureAlertValueText.Text = $"{_appSettings.CpuTemperatureAlertThresholdCelsius:0} °C";
        CpuTemperatureSoundAlertCheckBox.IsChecked = _appSettings.CpuTemperatureSoundAlertEnabled;
        SelectCpuAlertIntervalItem(_appSettings.CpuTemperatureAlertIntervalSeconds);
        NormalizePowerScheduleSettings(_appSettings);
        SelectPowerScheduleControls();
        PowerScheduleToggle.IsChecked = _appSettings.PowerScheduleEnabled;
        ForcePowerActionToggle.IsChecked = _appSettings.ForcePowerAction;
        UpdatePowerScheduleStatusDisplay();
        _loadingSettings = false;

        InitializeDashboardLayout();
        RestoreDesktopWidgets();

        _ = CheckForUpdateInBackgroundAsync(TimeSpan.FromSeconds(4));
        _updateCheckTimer.Start();

        IReadOnlyList<DeviceEntry> savedDevices = await _storageService.LoadAsync();
        foreach (DeviceEntry device in savedDevices.OrderBy(device => device.Name))
        {
            Devices.Add(device);
        }

        UpdateDeviceCount();
        await RefreshBackgroundMonitoringServiceAsync();
        await RefreshCpuPackagePowerAsync();
        await RefreshGpuPowerAsync();
        await RefreshCpuTemperatureAsync();
        RefreshSystemResourceUsage();
        await RefreshSystemHardwareInventoryAsync();
        await RefreshSpoolerStatusAsync();
        _ = RefreshSystemHealthDetailsAsync(forcePublicRefresh: true);
        await RefreshPowerPlansAsync();

        _hardwareTimer.Start();
        _serviceTimer.Start();
        _pingCountdownTimer.Start();

        if (Devices.Count > 0 && !IsBackgroundServiceRunning)
        {
            await RefreshAllDevicesAsync();
        }

        UpdateAutomaticMonitoringState();

        // External utility metadata is refreshed on every dashboard start without delaying the main UI.
        _ = RefreshExternalWindowsToolsAsync();
        _ = ScanNetworkPrintersAfterStartupAsync();
        _ = RefreshWindowsActivationStatusAsync();
    }

    private static WpfBrush GetThemeBrush(string resourceKey, WpfBrush fallback)
    {
        return WpfApplication.Current.Resources[resourceKey] as WpfBrush ?? fallback;
    }

    private void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowNavigationPage(NavigationList.SelectedIndex);
    }

    private void ShowNavigationPage(int selectedIndex)
    {
        int index = selectedIndex < 0 ? 0 : selectedIndex;

        // Hidden rather than Collapsed: the tiles stay laid out so desktop widgets keep updating.
        SystemStatsPage.Visibility = index == 0 ? Visibility.Visible : Visibility.Hidden;
        NetworkDiagnosticsPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        UtilityToolsPage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PowerBatteryPage.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

        switch (index)
        {
            case 1:
                HeaderTitleText.Text = "Network Diagnostics";
                HeaderSubtitleText.Text = "Scanner, monitoring and connection details";
                HeaderPrimaryActionButton.Content = "IP Scanner";
                HeaderPrimaryActionButton.Tag = "\uE721";
                NetworkDiagnosticsPage.ScrollToTop();
                _ = RefreshManagedDevicesForNetworkPageAsync();
                break;
            case 2:
                HeaderTitleText.Text = "Utility Tools";
                HeaderSubtitleText.Text = "Technician shortcuts, command runners and maintenance";
                HeaderPrimaryActionButton.Content = "Task Manager";
                HeaderPrimaryActionButton.Tag = "\uE7C4";
                UtilityToolsPage.ScrollToTop();
                break;
            case 3:
                HeaderTitleText.Text = "Power & Battery";
                HeaderSubtitleText.Text = "Power plans, scheduling and battery diagnostics";
                HeaderPrimaryActionButton.Content = "Battery Details";
                HeaderPrimaryActionButton.Tag = "\uE83F";
                PowerBatteryPage.ScrollToTop();
                _ = RefreshPowerPlansAsync();
                break;
            default:
                HeaderTitleText.Text = "System Stats";
                HeaderSubtitleText.Text = "Live health, hardware inventory and monitoring overview";
                HeaderPrimaryActionButton.Content = "Refresh";
                HeaderPrimaryActionButton.Tag = "\uE72C";
                SystemStatsPage.ScrollToTop();
                _ = RefreshSystemHardwareInventoryAsync();
                _ = RefreshWindowsActivationStatusAsync();
                break;
        }
    }

    private async void HeaderPrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (NavigationList.SelectedIndex)
        {
            case 1:
                OpenIpScannerButton_Click(sender, e);
                break;
            case 2:
                OpenTaskManagerButton_Click(sender, e);
                break;
            case 3:
                OpenBatteryDiagnosticsButton_Click(sender, e);
                break;
            default:
                StatusText.Text = "Refreshing system overview...";
                await RefreshCpuPackagePowerAsync();
                await RefreshGpuPowerAsync();
                await RefreshCpuTemperatureAsync();
                RefreshSystemResourceUsage();
                await RefreshSystemHardwareInventoryAsync();
                await RefreshSpoolerStatusAsync();
                await RefreshInstalledPrintersAsync();
                await RefreshBackgroundMonitoringServiceAsync();
                await RefreshSystemHealthDetailsAsync(forcePublicRefresh: true);
                await RefreshWindowsActivationStatusAsync();
                StatusText.Text = "System overview refreshed.";
                break;
        }
    }

    private async void ThemePreferenceRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || !IsLoaded || sender is not WpfRadioButton radioButton || radioButton.Tag is not string preference)
        {
            return;
        }

        _appSettings.UiThemePreference = ThemeManager.NormalizePreference(preference);
        _appliedDarkTheme = ThemeManager.Apply(_appSettings.UiThemePreference);
        await RefreshCpuTemperatureAsync();
        RefreshSystemResourceUsage();
        await RefreshWindowsActivationStatusAsync();
        UpdatePowerScheduleStatusDisplay();

        try
        {
            await _settingsService.SaveAsync(_appSettings);
            StatusText.Text = _appSettings.UiThemePreference == ThemeManager.SystemPreference
                ? "Theme follows Windows."
                : $"{_appSettings.UiThemePreference} theme enabled.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save UI theme", exception);
            StatusText.Text = "Theme changed, but the preference could not be saved.";
        }
    }

    private void SelectThemePreference(string preference)
    {
        string normalized = ThemeManager.NormalizePreference(preference);
        SystemThemeRadio.IsChecked = normalized == ThemeManager.SystemPreference;
        LightThemeRadio.IsChecked = normalized == ThemeManager.LightPreference;
        DarkThemeRadio.IsChecked = normalized == ThemeManager.DarkPreference;
    }

    private void RefreshSystemThemeIfNeeded()
    {
        if (!string.Equals(_appSettings.UiThemePreference, ThemeManager.SystemPreference, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool windowsDarkTheme = ThemeManager.IsWindowsAppThemeDark();
        if (windowsDarkTheme == _appliedDarkTheme)
        {
            return;
        }

        _appliedDarkTheme = ThemeManager.Apply(ThemeManager.SystemPreference);
        _ = RefreshCpuTemperatureAsync();
        RefreshSystemResourceUsage();
        UpdatePowerScheduleStatusDisplay();
        StatusText.Text = "Theme updated to match Windows.";
    }

    private void ApplyResponsiveWindowSizing()
    {
        Rect workArea = SystemParameters.WorkArea;

        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return;
        }

        // 1280x720 and 1366x768-class displays have very little spare
        // vertical space once the taskbar and window chrome are included.
        // Maximize on these screens so WPF uses the full Windows work area.
        if (workArea.Width <= 1366 || workArea.Height <= 740)
        {
            WindowState = System.Windows.WindowState.Maximized;
            return;
        }

        Width = Math.Min(1280, Math.Max(MinWidth, workArea.Width - 80));
        Height = Math.Min(800, Math.Max(MinHeight, workArea.Height - 80));
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowApplicationExit)
        {
            e.Cancel = true;
            DeviceEditorOverlay.Visibility = Visibility.Collapsed;
            ShowInTaskbar = false;
            Hide();

            if (!_trayHintShown && _trayIcon is not null && _appSettings.DesktopNotificationsEnabled)
            {
                _trayHintShown = true;
                _trayIcon.ShowBalloonTip(
                    5000,
                    "Windows Utility by Sajith",
                    "The dashboard is still running in the notification area. Background monitoring continues in the Windows service even if the dashboard exits.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }

            return;
        }

        _hardwareTimer.Stop();
        _serviceTimer.Stop();
        _deviceMonitorTimer.Stop();
        _pingCountdownTimer.Stop();
        DeviceEditorOverlay.Visibility = Visibility.Collapsed;
        _hardwareMonitorService.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayStatusIcon?.Dispose();
        _trayStatusIcon = null;
        _trayBaseIcon?.Dispose();
        _trayBaseIcon = null;
        ClearDashboardTrayReadyMarker();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "Windows Utility - checking service",
                Visible = _appSettings.TrayIconEnabled
            };

            string? processPath = Environment.ProcessPath;
            System.Drawing.Icon? trayIcon = null;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
            }

            _trayBaseIcon = trayIcon ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
            _trayIcon.Icon = _trayBaseIcon;

            System.Windows.Forms.ContextMenuStrip menu = new();
            _trayServiceStatusItem = new System.Windows.Forms.ToolStripMenuItem("Service: Checking...") { Enabled = false };
            _trayMonitoringStatusItem = new System.Windows.Forms.ToolStripMenuItem("Monitoring: Checking...") { Enabled = false };
            _trayHeartbeatStatusItem = new System.Windows.Forms.ToolStripMenuItem("Heartbeat: Checking...") { Enabled = false };
            menu.Items.Add(_trayServiceStatusItem);
            menu.Items.Add(_trayMonitoringStatusItem);
            menu.Items.Add(_trayHeartbeatStatusItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Refresh service status", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = RefreshBackgroundMonitoringServiceAsync())));
            menu.Items.Add("Open dashboard", null, (_, _) => Dispatcher.Invoke(ShowDashboardFromTray));
            menu.Items.Add("Open service log", null, (_, _) => Dispatcher.Invoke(new Action(_backgroundServiceManager.OpenServiceLog)));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit dashboard", null, (_, _) => Dispatcher.Invoke(ExitDashboard));
            menu.Opening += (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = RefreshBackgroundMonitoringServiceAsync()));
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(() =>
            {
                UpdateTrayServiceStatus();
                ShowTrayServiceStatusBalloon();
                ShowDashboardFromTray();
            });
            UpdateTrayServiceStatus();
            MarkDashboardTrayReady();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("System tray", exception);
        }
    }

    private void UpdateTrayServiceStatus()
    {
        if (_trayIcon is null)
        {
            return;
        }

        bool serviceRunning = _backgroundServiceInfo?.IsRunning == true;
        string serviceStatus = _backgroundServiceInfo?.Status ?? "Checking";
        bool heartbeatFresh = _backgroundServiceSnapshot is not null &&
            _backgroundServiceSnapshot.UpdatedAt != default &&
            DateTime.Now - _backgroundServiceSnapshot.UpdatedAt <= TimeSpan.FromSeconds(15);

        string monitoringStatus;
        if (!serviceRunning)
        {
            monitoringStatus = _backgroundServiceInfo?.Exists == false ? "Unavailable - service not installed" : "Unavailable";
        }
        else if (!heartbeatFresh)
        {
            monitoringStatus = "Service running - heartbeat stale";
        }
        else if (_backgroundServiceSnapshot!.MonitoringEnabled)
        {
            monitoringStatus = $"Active - {_backgroundServiceSnapshot.DeviceCount} managed device(s)";
        }
        else
        {
            monitoringStatus = "Service running - ping monitoring paused";
        }

        string heartbeat = _backgroundServiceSnapshot is null || _backgroundServiceSnapshot.UpdatedAt == default
            ? "No heartbeat yet"
            : heartbeatFresh
                ? $"Updated {FormatTrayAge(DateTime.Now - _backgroundServiceSnapshot.UpdatedAt)} ago"
                : $"Last update {_backgroundServiceSnapshot.UpdatedAt:dd MMM yyyy HH:mm:ss}";

        if (_trayServiceStatusItem is not null)
        {
            _trayServiceStatusItem.Text = $"Service: {serviceStatus}";
        }

        if (_trayMonitoringStatusItem is not null)
        {
            _trayMonitoringStatusItem.Text = $"Monitoring: {monitoringStatus}";
        }

        if (_trayHeartbeatStatusItem is not null)
        {
            _trayHeartbeatStatusItem.Text = $"Heartbeat: {heartbeat}";
        }

        string tooltip = $"Windows Utility | Service {serviceStatus} | {monitoringStatus}";
        _trayIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..60] + "...";
        UpdateDashboardTrayStatusIcon(serviceRunning, heartbeatFresh);
    }

    private void UpdateDashboardTrayStatusIcon(bool serviceRunning, bool heartbeatFresh)
    {
        if (_trayIcon is null || _trayBaseIcon is null)
        {
            return;
        }

        System.Drawing.Color statusColor = serviceRunning && heartbeatFresh
            ? System.Drawing.Color.FromArgb(52, 199, 89)
            : serviceRunning
                ? System.Drawing.Color.FromArgb(255, 159, 10)
                : System.Drawing.Color.FromArgb(255, 59, 48);

        using System.Drawing.Bitmap bitmap = new(32, 32);
        using System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);
        graphics.DrawIcon(_trayBaseIcon, new System.Drawing.Rectangle(0, 0, 32, 32));
        using System.Drawing.SolidBrush outline = new(System.Drawing.Color.White);
        using System.Drawing.SolidBrush fill = new(statusColor);
        graphics.FillEllipse(outline, 19, 19, 13, 13);
        graphics.FillEllipse(fill, 21, 21, 9, 9);

        IntPtr iconHandle = bitmap.GetHicon();
        try
        {
            using System.Drawing.Icon temporary = System.Drawing.Icon.FromHandle(iconHandle);
            System.Drawing.Icon newIcon = (System.Drawing.Icon)temporary.Clone();
            System.Drawing.Icon? oldIcon = _trayStatusIcon;
            _trayStatusIcon = newIcon;
            _trayIcon.Icon = newIcon;
            oldIcon?.Dispose();
        }
        finally
        {
            DestroyTrayIconHandle(iconHandle);
        }
    }

    private void ShowTrayServiceStatusBalloon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        string serviceStatus = _backgroundServiceInfo?.Status ?? "Checking";
        bool heartbeatFresh = _backgroundServiceSnapshot is not null &&
            _backgroundServiceSnapshot.UpdatedAt != default &&
            DateTime.Now - _backgroundServiceSnapshot.UpdatedAt <= TimeSpan.FromSeconds(15);
        string monitoringStatus = _backgroundServiceInfo?.IsRunning != true
            ? "Unavailable"
            : !heartbeatFresh
                ? "Service running - heartbeat stale"
                : _backgroundServiceSnapshot!.MonitoringEnabled
                    ? $"Active - {_backgroundServiceSnapshot.DeviceCount} managed device(s)"
                    : "Service running - ping monitoring paused";

        if (!_appSettings.DesktopNotificationsEnabled)
        {
            return;
        }

        _trayIcon.ShowBalloonTip(
            4500,
            "Windows Utility background monitoring",
            $"Service: {serviceStatus}\nMonitoring: {monitoringStatus}",
            _backgroundServiceInfo?.IsRunning == true
                ? System.Windows.Forms.ToolTipIcon.Info
                : System.Windows.Forms.ToolTipIcon.Warning);
    }

    private static string FormatTrayAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalSeconds < 60
            ? $"{Math.Max(0, (int)age.TotalSeconds)}s"
            : age.TotalMinutes < 60
                ? $"{(int)age.TotalMinutes}m"
                : $"{(int)age.TotalHours}h";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "DestroyIcon", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyTrayIconHandle(IntPtr handle);

    private static void MarkDashboardTrayReady()
    {
        try
        {
            SharedDataPaths.EnsureDirectories();
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
            File.WriteAllText(
                SharedDataPaths.DashboardTrayReadyFilePath,
                $"{process.Id}|{process.SessionId}");
        }
        catch
        {
        }
    }

    private static void ClearDashboardTrayReadyMarker()
    {
        try
        {
            string path = SharedDataPaths.DashboardTrayReadyFilePath;
            if (!File.Exists(path))
            {
                return;
            }

            string text = File.ReadAllText(path).Trim();
            string[] parts = text.Split('|');
            if (parts.Length > 0 && int.TryParse(parts[0], out int pid) && pid != Environment.ProcessId)
            {
                return;
            }

            File.Delete(path);
        }
        catch
        {
        }
    }

    private void ShowDashboardFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>
    /// Restores and focuses the dashboard from another process's request. Windows refuses to
    /// steal foreground focus for a window the user did not just interact with, so the Topmost
    /// toggle is the standard way to force it in front.
    /// </summary>
    public void BringToFront()
    {
        ShowDashboardFromTray();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitDashboard()
    {
        _allowApplicationExit = true;
        Close();
    }

    private void AddDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowDeviceEditor(existingDevice: null);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Add device editor", exception);
            WpfMessageBox.Show(
                $"The device editor could not be opened.\n\n{exception.Message}\n\nThe error was written to the application log.",
                "Add device",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void EditDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        DeviceEntry? selected = RequireSelectedDevice();
        if (selected is null)
        {
            return;
        }

        try
        {
            ShowDeviceEditor(selected);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Edit device editor", exception);
            WpfMessageBox.Show(
                $"The device editor could not be opened.\n\n{exception.Message}\n\nThe error was written to the application log.",
                "Edit device",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ShowDeviceEditor(DeviceEntry? existingDevice)
    {
        // Do not force WPF's InputLanguage attached property here. On Windows installations
        // that use a BCP-47/custom locale, the active keyboard can report LANGID 0x1000
        // (LOCALE_CUSTOM_UNSPECIFIED). WPF's legacy InputLanguageSource converts that
        // numeric LANGID back through CultureInfo and can throw CultureNotFoundException.
        // Leaving the TextBoxes on WPF's default invariant InputLanguage lets Windows keep
        // the user's real keyboard layout while avoiding the unsupported LCID conversion.
        InputLanguageManager.SetRestoreInputLanguage(DeviceEditorNameTextBox, false);
        InputLanguageManager.SetRestoreInputLanguage(DeviceEditorIpTextBox, false);

        _deviceBeingEdited = existingDevice;
        DeviceEditorTitleText.Text = existingDevice is null ? "Add device" : "Edit device";
        DeviceEditorNameTextBox.Text = existingDevice?.Name ?? string.Empty;
        DeviceEditorIpTextBox.Text = existingDevice?.IpAddress ?? string.Empty;
        DeviceEditorValidationText.Text = string.Empty;
        DeviceEditorOverlay.Visibility = Visibility.Visible;

        // Defer focus until the overlay is rendered. This avoids a focus/input-language
        // transition occurring in the middle of the Edit button's routed input event.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (DeviceEditorOverlay.Visibility != Visibility.Visible)
            {
                return;
            }

            DeviceEditorNameTextBox.Focus();
            DeviceEditorNameTextBox.SelectAll();
        }));
    }

    private void CancelDeviceEditorButton_Click(object sender, RoutedEventArgs e) => CloseDeviceEditor();

    private void DeviceEditorOverlay_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseDeviceEditor();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter &&
                 Keyboard.FocusedElement is WpfTextBox &&
                 !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            SaveDeviceEditorButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void CloseDeviceEditor()
    {
        DeviceEditorOverlay.Visibility = Visibility.Collapsed;
        DeviceEditorValidationText.Text = string.Empty;
        _deviceBeingEdited = null;
        DeviceGrid.Focus();
    }

    private async void SaveDeviceEditorButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string name = DeviceEditorNameTextBox.Text.Trim();
            string ipAddress = DeviceEditorIpTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                DeviceEditorValidationText.Text = "Enter a device name.";
                DeviceEditorNameTextBox.Focus();
                return;
            }

            if (!IPAddress.TryParse(ipAddress, out IPAddress? parsedAddress) ||
                parsedAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                DeviceEditorValidationText.Text = "Enter a valid IPv4 address, for example 192.168.1.50.";
                DeviceEditorIpTextBox.Focus();
                DeviceEditorIpTextBox.SelectAll();
                return;
            }

            string normalizedIpAddress = parsedAddress.ToString();
            Guid editingId = _deviceBeingEdited?.Id ?? Guid.Empty;
            bool duplicate = Devices.Any(device =>
                device.Id != editingId &&
                string.Equals(device.IpAddress, normalizedIpAddress, StringComparison.OrdinalIgnoreCase));

            if (duplicate)
            {
                DeviceEditorValidationText.Text = "A device with this IP address already exists.";
                DeviceEditorIpTextBox.Focus();
                DeviceEditorIpTextBox.SelectAll();
                return;
            }

            DeviceEntry deviceToRefresh;
            if (_deviceBeingEdited is null)
            {
                deviceToRefresh = new DeviceEntry
                {
                    Id = Guid.NewGuid(),
                    Name = name,
                    IpAddress = normalizedIpAddress
                };

                Devices.Add(deviceToRefresh);
                DeviceGrid.SelectedItem = deviceToRefresh;
                UpdateDeviceCount();
                StatusText.Text = $"Added {deviceToRefresh.Name}. Checking the device now...";
            }
            else
            {
                deviceToRefresh = _deviceBeingEdited;
                deviceToRefresh.Name = name;
                deviceToRefresh.IpAddress = normalizedIpAddress;
                deviceToRefresh.Status = "Not checked";
                deviceToRefresh.Hostname = "Not available";
                deviceToRefresh.MacAddress = "Not available";
                deviceToRefresh.RoundTripTime = null;
                deviceToRefresh.LastChecked = null;
                deviceToRefresh.LastError = string.Empty;
                StatusText.Text = $"Updated {deviceToRefresh.Name}. Checking the device now...";
            }

            CloseDeviceEditor();
            await SaveDevicesAsync();
            UpdateSelectedDeviceDetails();
            await RefreshDeviceAsync(deviceToRefresh);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save device", exception);
            string message = $"The device could not be saved: {exception.Message}. Details were written to the application log.";
            if (DeviceEditorOverlay.Visibility == Visibility.Visible)
            {
                DeviceEditorValidationText.Text = message;
            }
            else
            {
                WpfMessageBox.Show(message, "Save device", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void RemoveDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        DeviceEntry? selected = RequireSelectedDevice();
        if (selected is null)
        {
            return;
        }

        MessageBoxResult answer = WpfMessageBox.Show(
            $"Remove {selected.Name} and its connection/restart history?",
            "Remove device",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        Devices.Remove(selected);
        await _logService.DeleteAsync(selected.Id);
        UpdateDeviceCount();
        UpdateSelectedDeviceDetails();
        await SaveDevicesAsync();
        StatusText.Text = "Device connection/restart history removed.";
    }

    private async void RefreshSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        DeviceEntry? selected = RequireSelectedDevice();
        if (selected is not null)
        {
            await RefreshDeviceAsync(selected);
        }
    }

    private async void RefreshAllButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAllDevicesAsync();
        if (AutomaticMonitoringCheckBox.IsChecked == true && !IsBackgroundServiceRunning)
        {
            ScheduleNextAutomaticPing();
        }
    }

    private void ViewLogButton_Click(object sender, RoutedEventArgs e) => OpenSelectedDeviceLog();

    private void DeviceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedDevice is not null)
        {
            OpenSelectedDeviceLog();
        }
    }

    private void OpenSelectedDeviceLog()
    {
        DeviceEntry? selected = RequireSelectedDevice();
        if (selected is null)
        {
            return;
        }

        DeviceLogWindow logWindow = new(selected, _logService) { Owner = this };
        logWindow.ShowDialog();
    }

    private async Task RefreshAllDevicesAsync()
    {
        if (_refreshingDevices || Devices.Count == 0)
        {
            return;
        }

        _refreshingDevices = true;
        StatusText.Text = $"Checking {Devices.Count} device(s)...";

        try
        {
            using SemaphoreSlim limiter = new(initialCount: 8, maxCount: 8);
            IEnumerable<Task> checks = Devices.Select(async device =>
            {
                await limiter.WaitAsync();
                try
                {
                    await RefreshDeviceAsync(device, updateOverallStatus: false);
                }
                finally
                {
                    limiter.Release();
                }
            });

            await Task.WhenAll(checks);
            int onlineCount = Devices.Count(device => device.Status == "Online");
            StatusText.Text = $"Device check complete. {onlineCount} of {Devices.Count} online. Connection-loss incidents and inferred shutdown/restart events are logged.";
        }
        finally
        {
            _refreshingDevices = false;
            UpdateSelectedDeviceDetails();
        }
    }

    private async Task RefreshDeviceAsync(DeviceEntry device, bool updateOverallStatus = true)
    {
        if (device.IsChecking)
        {
            return;
        }

        string previousStatus = device.Status;
        DateTime? previousLastChecked = device.LastChecked;
        device.IsChecking = true;
        device.Status = "Checking";
        if (updateOverallStatus)
        {
            StatusText.Text = $"Checking {device.Name}...";
        }

        DateTime checkedAt = DateTime.Now;

        try
        {
            NetworkCheckResult result = await _networkService.CheckAsync(device.IpAddress);
            string status = result.IsOnline ? "Online" : "Offline";

            device.Status = status;
            device.Hostname = result.Hostname;
            device.MacAddress = result.MacAddress;
            device.RoundTripTime = result.RoundTripTime;
            device.LastChecked = checkedAt;
            device.LastError = result.ErrorMessage;

            if (!IsBackgroundServiceRunning)
            {
                await TrackDashboardDeviceIncidentAsync(
                    device,
                    result.IsOnline,
                    checkedAt,
                    previousStatus,
                    previousLastChecked,
                    result.ErrorMessage);
            }

            if (updateOverallStatus)
            {
                StatusText.Text = result.IsOnline
                    ? $"{device.Name} is online."
                    : $"{device.Name} did not respond. A connection incident is recorded when a loss is detected.";
            }
        }
        catch (Exception exception)
        {
            device.Status = "Error";
            device.RoundTripTime = null;
            device.LastChecked = checkedAt;
            device.LastError = exception.Message;

            if (!IsBackgroundServiceRunning)
            {
                await TrackDashboardDeviceIncidentAsync(
                    device,
                    false,
                    checkedAt,
                    previousStatus,
                    previousLastChecked,
                    exception.Message);
            }

            if (updateOverallStatus)
            {
                StatusText.Text = $"Could not check {device.Name}.";
            }
        }
        finally
        {
            device.IsChecking = false;
            if (ReferenceEquals(device, SelectedDevice))
            {
                UpdateSelectedDeviceDetails();
            }
        }
    }

    private async Task TrackDashboardDeviceIncidentAsync(
        DeviceEntry device,
        bool isOnline,
        DateTime checkedAt,
        string previousStatus,
        DateTime? previousLastChecked,
        string errorMessage)
    {
        try
        {
            List<DevicePingLogEntry> incidents = (await _logService.LoadAsync(device.Id))
                .OrderByDescending(entry => entry.LostAt)
                .ToList();
            DevicePingLogEntry? activeIncident = incidents.FirstOrDefault(entry => entry.IsActive);

            if (isOnline)
            {
                if (activeIncident is not null)
                {
                    activeIncident.RecoveredAt = checkedAt;
                    activeIncident.SchemaVersion = Math.Max(activeIncident.SchemaVersion, 3);
                    activeIncident.EventType = "ShutdownRestartInferred";
                    activeIncident.EventMessage =
                        $"Device turned off / restarted at {activeIncident.LostAt:dd MMM yyyy HH:mm:ss} " +
                        $"(inferred from loss of reachability). Connection restored at {checkedAt:dd MMM yyyy HH:mm:ss}. " +
                        "A network outage can produce the same symptom when remote boot telemetry is unavailable.";
                    await _logService.ReplaceAsync(device.Id, incidents);
                }

                return;
            }

            if (activeIncident is not null)
            {
                return;
            }

            DateTime? lastRecovery = incidents
                .Where(entry => entry.RecoveredAt.HasValue)
                .Select(entry => entry.RecoveredAt)
                .OrderByDescending(value => value)
                .FirstOrDefault();
            DateTime? activeSince = lastRecovery;

            if (!activeSince.HasValue &&
                string.Equals(previousStatus, "Online", StringComparison.OrdinalIgnoreCase) &&
                previousLastChecked.HasValue)
            {
                activeSince = previousLastChecked.Value;
            }

            DevicePingLogEntry incident = new()
            {
                SchemaVersion = 3,
                LostAt = checkedAt,
                ActiveDurationBeforeLossSeconds = activeSince.HasValue
                    ? Math.Max(0, (checkedAt - activeSince.Value).TotalSeconds)
                    : null,
                ErrorMessage = errorMessage,
                EventType = "ReachabilityLoss",
                EventMessage =
                    $"Device became unreachable at {checkedAt:dd MMM yyyy HH:mm:ss}. " +
                    "A shutdown, restart or network interruption is possible; the exact cause is inferred from reachability monitoring."
            };
            incidents.Insert(0, incident);
            await _logService.ReplaceAsync(device.Id, incidents);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Dashboard device incident", exception);
        }
    }

    private void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedDeviceDetails();
        if (SelectedDevice is not null)
        {
            SidePanelTabs.SelectedIndex = 0;
        }
    }

    private void UpdateSelectedDeviceDetails()
    {
        DeviceEntry? selected = SelectedDevice;
        bool hasSelection = selected is not null;

        NoSelectionText.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        DeviceDetailsPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

        if (selected is null)
        {
            return;
        }

        DetailNameText.Text = selected.Name;
        DetailIpText.Text = selected.IpAddress;
        DetailStatusText.Text = selected.Status;
        DetailHostnameText.Text = selected.Hostname;
        DetailMacText.Text = selected.MacAddress;
        DetailResponseText.Text = selected.ResponseDisplay;
        DetailLastCheckedText.Text = selected.LastCheckedDisplay;
        DetailErrorText.Text = string.IsNullOrWhiteSpace(selected.LastError) ? "None" : selected.LastError;
    }

    private void OpenWebPageButton_Click(object sender, RoutedEventArgs e) =>
        RunDeviceShortcut(device => _windowsUtilityService.OpenDeviceWebPage(device.IpAddress));

    private void OpenNetworkShareButton_Click(object sender, RoutedEventArgs e) =>
        RunDeviceShortcut(device => _windowsUtilityService.OpenNetworkShare(device.IpAddress));

    private void RemoteDesktopButton_Click(object sender, RoutedEventArgs e) =>
        RunDeviceShortcut(device => _windowsUtilityService.OpenRemoteDesktop(device.IpAddress));

    private void RunDeviceShortcut(Action<DeviceEntry> action)
    {
        DeviceEntry? selected = RequireSelectedDevice();
        if (selected is null)
        {
            return;
        }

        try
        {
            action(selected);
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(exception.Message, "Unable to open shortcut", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenDevicesAndPrintersButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_windowsUtilityService.OpenDevicesAndPrinters, "Devices and Printers");

    private void OpenTaskManagerButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_windowsUtilityService.OpenTaskManager, "Task Manager");

    private void OpenCommandPromptButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(() => _windowsUtilityService.OpenCommandPrompt(elevated: false), "Command Prompt");

    private void OpenCommandPromptAdminButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(() => _windowsUtilityService.OpenCommandPrompt(elevated: true), "Command Prompt (Administrator)");

    private void OpenPowerShellButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(() => _windowsUtilityService.OpenPowerShell(elevated: false), "PowerShell");

    private void OpenPowerShellAdminButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(() => _windowsUtilityService.OpenPowerShell(elevated: true), "PowerShell (Administrator)");

    private void OpenDiskCleanupButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_windowsUtilityService.OpenDiskCleanupElevated, "Disk Cleanup (Administrator)");

    private void OpenDiskPartButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_windowsUtilityService.OpenDiskPartElevated, "DiskPart (Administrator)");

    private void OpenDiskManagementButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_windowsUtilityService.OpenDiskManagementElevated, "Disk Management (Administrator)");

    private void OpenStorageSenseButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_windowsUtilityService.OpenStorageSenseSettings, "Storage Sense");

    private void OpenSmartAppControlButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_windowsUtilityService.OpenSmartAppControlSettings, "Smart App Control");

    private void OpenWindowsRepairCenterButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WindowsRepairWindow window = new() { Owner = this };
            window.ShowDialog();
            StatusText.Text = "Closed Windows Repair & Recovery.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Open Windows Repair & Recovery", exception);
            WpfMessageBox.Show(this, exception.Message, "Windows Repair & Recovery", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RepairSfcButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsRepairShortcut("sfc /scannow", "SFC system file repair");

    private void RepairDismCheckButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsRepairShortcut("DISM.exe /Online /Cleanup-Image /CheckHealth", "DISM CheckHealth");

    private void RepairDismScanButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsRepairShortcut("DISM.exe /Online /Cleanup-Image /ScanHealth", "DISM ScanHealth");

    private void RepairDismRestoreButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsRepairShortcut("DISM.exe /Online /Cleanup-Image /RestoreHealth", "DISM RestoreHealth");

    private void RepairChkdskButton_Click(object sender, RoutedEventArgs e)
    {
        string systemDrive = Environment.GetEnvironmentVariable("SystemDrive")?.Trim().TrimEnd('\\', '/') ?? "C:";
        if (string.IsNullOrWhiteSpace(systemDrive) || systemDrive.Length < 2 || systemDrive[1] != ':')
        {
            systemDrive = "C:";
        }

        MessageBoxResult result = WpfMessageBox.Show(
            this,
            $"Run CHKDSK {systemDrive} /f /r?\n\nThis can take a long time. If the Windows volume is in use, Windows may ask to schedule the repair for the next restart.",
            "CHKDSK system drive",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result == MessageBoxResult.Yes)
        {
            RunWindowsRepairShortcut($"chkdsk {systemDrive} /f /r", $"CHKDSK {systemDrive}");
        }
    }

    private void RunWindowsRepairShortcut(string command, string title)
    {
        try
        {
            _windowsUtilityService.RunCommandPromptCommand(command, elevated: true);
            StatusText.Text = $"Opened {title} in Administrator Command Prompt.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException($"Windows repair shortcut - {title}", exception);
            WpfMessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenDiskPreparationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DiskPreparationWindow window = new() { Owner = this };
            window.ShowDialog();
            StatusText.Text = "Closed DiskPart & disk initialization.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Open disk preparation", exception);
            WpfMessageBox.Show(exception.Message, "Disk preparation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CommandModeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (CmdModeText is not null && CmdAdminToggle is not null)
        {
            CmdModeText.Text = CmdAdminToggle.IsChecked == true ? "Administrator" : "Normal user";
        }

        if (PowerShellModeText is not null && PowerShellAdminToggle is not null)
        {
            PowerShellModeText.Text = PowerShellAdminToggle.IsChecked == true ? "Administrator" : "Normal user";
        }
    }

    private void NetworkIpConfigButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkTroubleshootingCommand("ipconfig /all", "IP configuration");

    private void NetworkInterfaceConfigButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkTroubleshootingCommand("netsh interface ip show config", "Interface configuration");

    private void NetworkRouteTableButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkTroubleshootingCommand("route print", "Routing table");

    private void NetworkArpCacheButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkTroubleshootingCommand("arp -a", "ARP cache");

    private void NetworkConnectionsButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkTroubleshootingCommand("netstat -ano", "Network connections and listening ports");

    private void NetworkGetMacButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkTroubleshootingCommand("getmac /v", "MAC addresses");

    private void NetworkSharesButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkTroubleshootingCommand("net use", "Network shares");

    private void NetworkPingButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkTargetCommand("ping", "Ping");

    private void NetworkTraceRouteButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkTargetCommand("tracert", "Trace route");

    private void NetworkDnsLookupButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkTargetCommand("nslookup", "DNS lookup");

    private void NetworkReleaseRenewButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkRepairCommand(
            "ipconfig /release\r\nipconfig /renew",
            "Release and renew IP address",
            "This releases DHCP addresses and requests new ones. Network connectivity will be interrupted temporarily.",
            restartRequired: false);

    private void NetworkFlushDnsButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkRepairCommand(
            "ipconfig /flushdns",
            "Flush DNS cache",
            "This clears the Windows DNS resolver cache. Active connections are not normally interrupted.",
            restartRequired: false);

    private void NetworkResetStackButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkRepairCommand(
            "netsh winsock reset\r\nnetsh int ip reset",
            "Reset Winsock and TCP/IP",
            "This resets Windows networking components to their default state. A Windows restart is required afterwards.",
            restartRequired: true);

    private void NetworkClearArpButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkRepairCommand(
            "arp -d *",
            "Clear ARP cache",
            "This removes cached local IP-to-MAC mappings. Windows will rebuild them automatically as devices are contacted.",
            restartRequired: false);

    private void NetworkFullRepairButton_Click(object sender, RoutedEventArgs e) =>
        RunNetworkRepairCommand(
            "ipconfig /release\r\nipconfig /renew\r\nipconfig /flushdns\r\nnetsh winsock reset\r\nnetsh int ip reset\r\narp -d *",
            "Run full network repair",
            "This runs Release/Renew, Flush DNS, Winsock reset, TCP/IP reset and ARP-cache clear in order. Connectivity can drop and Windows must be restarted when the commands finish.",
            restartRequired: true);

    private void RunNetworkRepairCommand(string command, string title, string warning, bool restartRequired)
    {
        bool remoteSession = IsRemoteInteractiveSession();
        string remoteWarning = remoteSession
            ? "\n\nREMOTE SESSION DETECTED: this action may disconnect the current RDP/remote session."
            : string.Empty;
        string restartWarning = restartRequired
            ? "\n\nRestart Windows after the command completes for the reset to take full effect."
            : string.Empty;

        MessageBoxResult answer = WpfMessageBox.Show(
            this,
            $"{warning}{remoteWarning}{restartWarning}\n\nRun this network repair command as Administrator?",
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _windowsUtilityService.RunCommandPromptCommand(command, elevated: true);
            StatusText.Text = restartRequired
                ? $"Opened {title} as Administrator. Restart Windows when it completes."
                : $"Opened {title} as Administrator.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException($"Network repair - {title}", exception);
            WpfMessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsRemoteInteractiveSession()
    {
        try
        {
            if (System.Windows.Forms.SystemInformation.TerminalServerSession)
            {
                return true;
            }
        }
        catch
        {
        }

        string sessionName = Environment.GetEnvironmentVariable("SESSIONNAME") ?? string.Empty;
        return sessionName.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase);
    }

    private void RunNetworkTargetCommand(string command, string title)
    {
        string? target = GetValidatedNetworkCommandTarget();
        if (target is null)
        {
            return;
        }

        RunNetworkTroubleshootingCommand($"{command} {target}", title);
    }

    private string? GetValidatedNetworkCommandTarget()
    {
        string target = NetworkCommandTargetTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            WpfMessageBox.Show(this, "Enter a hostname or IP address first.", "Network troubleshooting", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        bool validIp = IPAddress.TryParse(target, out _);
        bool validHostname = Uri.CheckHostName(target) is UriHostNameType.Dns;
        if (!validIp && !validHostname)
        {
            WpfMessageBox.Show(this, "Enter only a valid hostname or IP address. Special command characters are not accepted.", "Network troubleshooting", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        return target;
    }

    private void RunNetworkTroubleshootingCommand(string command, string title)
    {
        try
        {
            _windowsUtilityService.RunCommandPromptCommand(command, elevated: false);
            StatusText.Text = $"Opened {title}: {command}";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException($"Network troubleshooting - {title}", exception);
            WpfMessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RunCmdCommandButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool elevated = CmdAdminToggle.IsChecked == true;
            _windowsUtilityService.RunCommandPromptCommand(CmdCommandTextBox.Text, elevated);
            StatusText.Text = elevated ? "Opened CMD command as Administrator." : "Opened CMD command as normal user.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("CMD command runner", exception);
            WpfMessageBox.Show(exception.Message, "CMD command runner", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RunPowerShellCommandButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool elevated = PowerShellAdminToggle.IsChecked == true;
            _windowsUtilityService.RunPowerShellCommand(PowerShellCommandTextBox.Text, elevated);
            StatusText.Text = elevated ? "Opened PowerShell command as Administrator." : "Opened PowerShell command as normal user.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("PowerShell command runner", exception);
            WpfMessageBox.Show(exception.Message, "PowerShell command runner", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshExternalWindowsToolsAsync()
    {
        try
        {
            ChrisTitusToolStatusText.Text = "Checking the official WinUtil launch command...";
            Win11DebloatStatusText.Text = "Checking the official Win11Debloat quick-launch command...";
            WinhanceStatusText.Text = "Checking the official Winhance install command...";

            await Task.WhenAll(
                _externalWindowsToolService.RefreshChrisTitusCommandAsync(),
                _externalWindowsToolService.RefreshWin11DebloatCommandAsync(),
                _externalWindowsToolService.RefreshWinhanceCommandAsync(),
                _externalWindowsToolService.RefreshMicrosoftActivationHelpUrlAsync());

            ChrisTitusToolStatusText.Text = $"{_externalWindowsToolService.ChrisTitusCommandStatus} Endpoint: {_externalWindowsToolService.ChrisTitusEndpoint}";
            Win11DebloatStatusText.Text = $"{_externalWindowsToolService.Win11DebloatCommandStatus} Endpoint: {_externalWindowsToolService.Win11DebloatEndpoint}";
            WinhanceStatusText.Text = $"{_externalWindowsToolService.WinhanceCommandStatus} Endpoint: {_externalWindowsToolService.WinhanceEndpoint}";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("External Windows tools refresh", exception);
            ChrisTitusToolStatusText.Text = "Could not refresh WinUtil metadata; embedded official fallback will be used.";
            Win11DebloatStatusText.Text = "Could not refresh Win11Debloat metadata; embedded official fallback will be used.";
            WinhanceStatusText.Text = "Could not refresh Winhance metadata; embedded official fallback will be used.";
        }
    }

    private async Task RefreshWindowsActivationStatusAsync()
    {
        try
        {
            WindowsActivationInfo activation = await _externalWindowsToolService.GetWindowsActivationInfoAsync();
            WindowsActivationStatusText.Text = activation.Display;
            SystemWindowsActivationStatusText.Text = activation.Display;

            WpfBrush activationBrush = activation.IsActivated
                ? GetThemeBrush("SuccessBrush", WpfBrushes.SeaGreen)
                : activation.IsGracePeriod
                    ? GetThemeBrush("WarningBrush", WpfBrushes.DarkOrange)
                    : activation.RequiresActivation
                        ? GetThemeBrush("DangerBrush", WpfBrushes.Red)
                        : NormalCpuTemperatureBrush;
            SystemWindowsActivationStatusText.Foreground = activationBrush;
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Windows activation display", exception);
            WindowsActivationStatusText.Text = "Activation status unavailable.";
            SystemWindowsActivationStatusText.Text = "Activation status unavailable.";
            SystemWindowsActivationStatusText.Foreground = NormalCpuTemperatureBrush;
        }
    }

    private void OpenWindowsActivationSettingsButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_externalWindowsToolService.OpenWindowsActivationSettings, "Windows Activation Settings");

    private void OpenWindowsActivationHelpButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_externalWindowsToolService.OpenMicrosoftActivationHelp, "Microsoft activation help");

    private void OpenChrisTitusProjectButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_externalWindowsToolService.OpenChrisTitusProject, "Chris Titus Tech WinUtil project");

    private void RunChrisTitusWinUtilButton_Click(object sender, RoutedEventArgs e)
    {
        string command = _externalWindowsToolService.ChrisTitusLaunchCommand;
        MessageBoxResult answer = WpfMessageBox.Show(
            "Chris Titus Tech WinUtil performs system-wide changes and will be launched in an elevated PowerShell window. " +
            "Windows Utility by Sajith refreshes the stable launch endpoint from the official ChrisTitusTech GitHub README at dashboard startup.\n\n" +
            $"Command:\n{command}\n\nContinue?",
            "Run Chris Titus Tech WinUtil",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _externalWindowsToolService.LaunchChrisTitusWinUtilElevated();
            StatusText.Text = "Opened Chris Titus Tech WinUtil in elevated PowerShell.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Launch Chris Titus Tech WinUtil", exception);
            WpfMessageBox.Show(
                exception.Message,
                "Chris Titus Tech WinUtil",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }


    private void OpenWin11DebloatProjectButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_externalWindowsToolService.OpenWin11DebloatProject, "Raphire Win11Debloat project");

    private void RunWin11DebloatButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmAndLaunchExternalPowerShellTool(
            "Raphire Win11Debloat",
            _externalWindowsToolService.Win11DebloatLaunchCommand,
            _externalWindowsToolService.LaunchWin11DebloatElevated,
            "the official Raphire/Win11Debloat GitHub README");
    }

    private void OpenWinhanceProjectButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_externalWindowsToolService.OpenWinhanceProject, "Winhance project");

    private void RunWinhanceButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmAndLaunchExternalPowerShellTool(
            "Winhance",
            _externalWindowsToolService.WinhanceLaunchCommand,
            _externalWindowsToolService.LaunchWinhanceElevated,
            "the official memstechtips/Winhance GitHub README");
    }

    private void ConfirmAndLaunchExternalPowerShellTool(
        string displayName,
        string command,
        Action launcher,
        string metadataSource)
    {
        MessageBoxResult answer = WpfMessageBox.Show(
            $"{displayName} can make system-wide changes and will run in an elevated PowerShell window. " +
            $"Windows Utility by Sajith refreshes its launch endpoint from {metadataSource} every time the dashboard starts.\n\n" +
            $"Command:\n{command}\n\nContinue?",
            $"Run {displayName}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            launcher();
            StatusText.Text = $"Opened {displayName} in elevated PowerShell.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException($"Launch {displayName}", exception);
            WpfMessageBox.Show(exception.Message, displayName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private async void OpenIpScannerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IpScannerWindow scannerWindow = new() { Owner = this };
            scannerWindow.ShowDialog();
            await ReloadManagedDevicesFromStorageAsync();
            if (Devices.Count > 0)
            {
                await RefreshAllDevicesAsync();
            }
            StatusText.Text = scannerWindow.ManagedDevicesChanged
                ? "Managed device changes saved and live status refreshed."
                : "Closed Network scanner & managed devices. Live status refreshed.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Network scanner and managed devices", exception);
            WpfMessageBox.Show(exception.Message, "Network scanner", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ReloadManagedDevicesFromStorageAsync()
    {
        Dictionary<Guid, DeviceEntry> currentRuntimeState = Devices.ToDictionary(device => device.Id);
        IReadOnlyList<DeviceEntry> savedDevices = await _storageService.LoadAsync();

        Devices.Clear();
        foreach (DeviceEntry device in savedDevices.OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (currentRuntimeState.TryGetValue(device.Id, out DeviceEntry? existing) &&
                string.Equals(existing.IpAddress, device.IpAddress, StringComparison.OrdinalIgnoreCase))
            {
                CopyRuntimeDeviceState(existing, device);
            }

            Devices.Add(device);
        }

        bool snapshotIsFresh = IsBackgroundServiceRunning &&
            _backgroundServiceSnapshot is not null &&
            DateTime.Now - _backgroundServiceSnapshot.UpdatedAt <= TimeSpan.FromSeconds(15);
        if (snapshotIsFresh)
        {
            ApplyBackgroundDeviceStatuses(_backgroundServiceSnapshot!);
        }

        UpdateDeviceCount();
        UpdateSelectedDeviceDetails();
    }

    private static void CopyRuntimeDeviceState(DeviceEntry source, DeviceEntry target)
    {
        target.Status = source.Status;
        target.Hostname = source.Hostname;
        target.MacAddress = source.MacAddress;
        target.RoundTripTime = source.RoundTripTime;
        target.LastChecked = source.LastChecked;
        target.LastError = source.LastError;
        target.IsChecking = false;
    }

    private async Task RefreshManagedDevicesForNetworkPageAsync()
    {
        if (!IsLoaded || _refreshingNetworkManagedDevicesPage)
        {
            return;
        }

        _refreshingNetworkManagedDevicesPage = true;
        try
        {
            await ReloadManagedDevicesFromStorageAsync();
            if (Devices.Count == 0)
            {
                StatusText.Text = "No managed IP addresses configured.";
                return;
            }

            // Always perform a direct check when the page is opened so a newly-added
            // device does not remain at 'Not checked' while waiting for the service's
            // next scheduled ping cycle. Incident logging remains owned by the service
            // whenever the service is running.
            await RefreshAllDevicesAsync();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Network diagnostics managed-device refresh", exception);
            StatusText.Text = $"Managed-device status refresh failed: {exception.Message}";
        }
        finally
        {
            _refreshingNetworkManagedDevicesPage = false;
        }
    }

    private void OpenStorageDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        StorageDiagnosticsWindow window = new() { Owner = this };
        window.ShowDialog();
    }

    private void OpenThermalLoadTestButton_Click(object sender, RoutedEventArgs e)
    {
        ThermalLoadTestWindow window = new() { Owner = this };
        window.ShowDialog();
        StatusText.Text = "Closed CPU & GPU Thermal Load Test.";
    }

    private void OpenBatteryDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        BatteryDiagnosticsWindow window = new() { Owner = this };
        window.ShowDialog();
    }

    private async void RefreshPowerPlansButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshPowerPlansAsync();
    }

    private async void SetActivePowerPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (PowerPlanComboBox.SelectedItem is not PowerPlanInfo selectedPlan)
        {
            WpfMessageBox.Show(
                "Select a Windows power plan first.",
                "Power plan",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SetActivePowerPlanButton.IsEnabled = false;
        try
        {
            await _powerPlanService.SetActivePowerPlanAsync(selectedPlan.SchemeGuid);
            StatusText.Text = $"{selectedPlan.Name} is now the active Windows power plan.";
            await RefreshPowerPlansAsync();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Set active power plan", exception);
            WpfMessageBox.Show(
                exception.Message,
                "Power plan",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetActivePowerPlanButton.IsEnabled = PowerPlanComboBox.Items.Count > 0;
        }
    }

    private void OpenPowerOptionsButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_powerPlanService.OpenPowerOptions, "Windows Power Options");

    private void OpenLidOptionsButton_Click(object sender, RoutedEventArgs e) =>
        RunWindowsTool(_powerPlanService.OpenLidAndPowerButtonOptions, "lid and power button options");

    private async Task RefreshPowerPlansAsync()
    {
        if (_refreshingPowerPlans || PowerPlanComboBox is null || ActivePowerPlanText is null)
        {
            return;
        }

        _refreshingPowerPlans = true;
        try
        {
            IReadOnlyList<PowerPlanInfo> plans = await _powerPlanService.GetPowerPlansAsync();
            PowerPlanInfo? activePlan = plans.FirstOrDefault(plan => plan.IsActive);

            PowerPlanComboBox.ItemsSource = plans;
            PowerPlanComboBox.SelectedItem = activePlan ?? plans.FirstOrDefault();
            SetActivePowerPlanButton.IsEnabled = plans.Count > 0;

            if (activePlan is null)
            {
                ActivePowerPlanText.Text = plans.Count == 0 ? "No plans detected" : "Active plan not identified";
                ActivePowerPlanDetailText.Text = plans.Count == 0
                    ? "Windows did not return any available power schemes."
                    : "Select a plan below and set it active.";
                return;
            }

            ActivePowerPlanText.Text = activePlan.Name;
            ActivePowerPlanDetailText.Text = $"Currently active in Windows • {activePlan.SchemeGuid}";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Power plan status", exception);
            ActivePowerPlanText.Text = "Unavailable";
            ActivePowerPlanDetailText.Text = exception.Message;
            PowerPlanComboBox.ItemsSource = null;
            SetActivePowerPlanButton.IsEnabled = false;
        }
        finally
        {
            _refreshingPowerPlans = false;
        }
    }

    private void RunWindowsTool(Action action, string toolName)
    {
        try
        {
            action();
            StatusText.Text = $"Opened {toolName}.";
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(exception.Message, "Windows tools", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ViewCpuTemperatureLogButton_Click(object sender, RoutedEventArgs e)
    {
        CpuTemperatureLogWindow logWindow = new(_cpuTemperatureLogService) { Owner = this };
        logWindow.ShowDialog();
        await ReloadCpuTemperatureLogsAsync();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        AboutWindow aboutWindow = new(_appSettings, _settingsService) { Owner = this };
        aboutWindow.ShowDialog();
    }

    /// <summary>
    /// Background check used at startup and every few hours while the dashboard runs. Never blocks the UI;
    /// an available update shows the dashboard banner unless the user snoozed that version.
    /// </summary>
    private async Task CheckForUpdateInBackgroundAsync(TimeSpan delay)
    {
        try
        {
            if (!_appSettings.AutoCheckForUpdates && !_appSettings.AutoInstallUpdates)
            {
                return;
            }

            await Task.Delay(delay);
            UpdateCheckResult result = await UpdateService.CheckAsync();
            if (result.Status == UpdateCheckStatus.Failed || !IsLoaded)
            {
                return;
            }

            ApplyUpdateCheckResult(result);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Background update check", exception);
        }
    }

    private void ApplyUpdateCheckResult(UpdateCheckResult result)
    {
        if (result.Status == UpdateCheckStatus.Failed)
        {
            return;
        }

        if (result.Status != UpdateCheckStatus.Available || result.LatestVersion is null)
        {
            CancelAutoInstallCountdown();
            _availableUpdate = null;
            SidebarUpdateButton.Content = "Check for updates";
            SidebarUpdateButton.Tag = "";
            UpdateBanner.Visibility = Visibility.Collapsed;
            return;
        }

        _availableUpdate = result;
        SidebarUpdateButton.Content = $"Update to {result.LatestVersion}";
        SidebarUpdateButton.Tag = "";

        DateTime? released = UpdateService.ParseReleaseDate(result.Manifest?.Released);
        UpdateBannerTitle.Text = $"Windows Utility {result.LatestVersion} is available";
        UpdateBannerDetail.Text = released is null
            ? $"You have {result.CurrentVersion}. Install it now or snooze this reminder."
            : $"Published {UpdateService.FormatReleaseDate(released.Value)}. You have {result.CurrentVersion}.";
        bool snoozed = IsUpdateAlertSnoozed(result.LatestVersion);
        UpdateBanner.Visibility = snoozed ? Visibility.Collapsed : Visibility.Visible;

        if (_appSettings.AutoInstallUpdates && !snoozed && !_installingUpdate && _autoInstallTimer?.IsEnabled != true)
        {
            StartAutoInstallCountdown();
        }
    }

    /// <summary>
    /// Automatic updates: count down on the banner so the user can postpone, then download, verify and install.
    /// The installer closes the dashboard and reopens it when it finishes.
    /// </summary>
    private void StartAutoInstallCountdown()
    {
        _autoInstallSecondsLeft = 60;
        _autoInstallTimer ??= CreateAutoInstallTimer();
        UpdateAutoInstallCountdownText();
        _autoInstallTimer.Start();
        ApplicationLogService.WriteMessage("Update", $"Automatic install of {_availableUpdate?.LatestVersion} scheduled in {_autoInstallSecondsLeft} seconds.");
    }

    private DispatcherTimer CreateAutoInstallTimer()
    {
        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) =>
        {
            _autoInstallSecondsLeft--;
            if (_autoInstallSecondsLeft > 0)
            {
                UpdateAutoInstallCountdownText();
                return;
            }

            timer.Stop();
            await InstallAvailableUpdateAsync();
        };
        return timer;
    }

    private void UpdateAutoInstallCountdownText()
    {
        UpdateBannerTitle.Text = $"Installing Windows Utility {_availableUpdate?.LatestVersion} automatically in {_autoInstallSecondsLeft} s";
        UpdateBannerDetail.Text = "The dashboard closes during the install and reopens when it is done. Choose Remind me later to postpone.";
    }

    private void CancelAutoInstallCountdown()
    {
        _autoInstallTimer?.Stop();
    }

    private async Task InstallAvailableUpdateAsync()
    {
        if (_installingUpdate || _availableUpdate?.Manifest is not UpdateManifest manifest)
        {
            return;
        }

        _installingUpdate = true;
        UpdateBanner.Visibility = Visibility.Visible;
        UpdateBannerTitle.Text = $"Downloading Windows Utility {_availableUpdate.LatestVersion}...";
        UpdateBannerDetail.Text = "Starting the download.";
        try
        {
            Progress<double> progress = new(value =>
                UpdateBannerDetail.Text = $"{value:P0} downloaded. The installer is checked against its published SHA-256 before it runs.");
            string installerPath = await UpdateService.DownloadAsync(manifest, progress, CancellationToken.None);

            UpdateBannerTitle.Text = "Installing the update...";
            UpdateBannerDetail.Text = "The dashboard will reopen when the install finishes.";
            UpdateService.LaunchInstaller(installerPath);
            _allowApplicationExit = true;
            WpfApplication.Current.Shutdown();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Automatic update install", exception);
            _installingUpdate = false;
            UpdateBannerTitle.Text = $"Windows Utility {_availableUpdate?.LatestVersion} could not be installed automatically";
            UpdateBannerDetail.Text = $"{exception.Message} It will be tried again at the next check, or use Update now.";
        }
    }

    private bool IsUpdateAlertSnoozed(Version latest)
    {
        return string.Equals(_appSettings.UpdateSnoozedVersion, latest.ToString(), StringComparison.Ordinal)
            && _appSettings.UpdateSnoozedUntilUtc is DateTime until
            && until > DateTime.UtcNow;
    }

    private void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e) => OpenUpdateWindow();

    private void UpdateBannerUpdateButton_Click(object sender, RoutedEventArgs e) => OpenUpdateWindow();

    private void OpenUpdateWindow()
    {
        // The window handles the install itself; resume the automatic countdown afterwards if still relevant.
        CancelAutoInstallCountdown();
        UpdateWindow updateWindow = new(_appSettings, _settingsService) { Owner = this };
        updateWindow.ShowDialog();
        if (updateWindow.LastResult is UpdateCheckResult result)
        {
            ApplyUpdateCheckResult(result);
        }
    }

    private void UpdateSnoozeButton_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateSnoozeButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = UpdateSnoozeButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private async void UpdateSnoozeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate?.LatestVersion is not Version latest
            || sender is not System.Windows.Controls.MenuItem { Tag: string tag }
            || !int.TryParse(tag, out int days))
        {
            return;
        }

        CancelAutoInstallCountdown();
        DateTime until = DateTime.Now.AddDays(days);
        _appSettings.UpdateSnoozedVersion = latest.ToString();
        _appSettings.UpdateSnoozedUntilUtc = until.ToUniversalTime();
        UpdateBanner.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Update reminder for {latest} snoozed until {until:dddd d MMMM, t}. Use \"Update to {latest}\" in the sidebar any time.";

        try
        {
            await _settingsService.SaveAsync(_appSettings);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save update snooze", exception);
        }
    }

    private async void FanDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Collecting fan diagnostics...";
            string report = await Task.Run(_hardwareMonitorService.GetFanDiagnosticReport);
            SharedDataPaths.EnsureDirectories();
            await File.WriteAllTextAsync(SharedDataPaths.FanDiagnosticsFilePath, report);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "notepad.exe",
                $"\"{SharedDataPaths.FanDiagnosticsFilePath}\"")
            {
                UseShellExecute = true
            });
            StatusText.Text = "Fan diagnostics opened in Notepad.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Fan diagnostics", exception);
            WpfMessageBox.Show(exception.Message, "Fan diagnostics", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RunPowerActionNowButton_Click(object sender, RoutedEventArgs e)
    {
        NormalizePowerScheduleSettings(_appSettings);
        bool restart = string.Equals(_appSettings.PowerScheduleAction, "Restart", StringComparison.OrdinalIgnoreCase);
        string actionName = restart ? "restart" : "shutdown";
        string forceText = _appSettings.ForcePowerAction
            ? " Force close apps is enabled; unsaved work can be lost."
            : " Windows will allow applications to prompt for unsaved work.";

        MessageBoxResult answer = WpfMessageBox.Show(
            $"{char.ToUpperInvariant(actionName[0])}{actionName[1..]} Windows now?{forceText}",
            $"Windows {actionName}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (restart)
            {
                _windowsUtilityService.ScheduleRestart(0, _appSettings.ForcePowerAction);
            }
            else
            {
                _windowsUtilityService.ScheduleShutdown(0, _appSettings.ForcePowerAction);
            }

            StatusText.Text = $"Windows {actionName} requested.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException($"Windows {actionName}", exception);
            WpfMessageBox.Show(exception.Message, "System power", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TogglePowerScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        _appSettings.PowerScheduleEnabled = !_appSettings.PowerScheduleEnabled;
        _appSettings.PowerScheduleLastTriggeredDate = string.Empty;
        _loadingSettings = true;
        PowerScheduleToggle.IsChecked = _appSettings.PowerScheduleEnabled;
        _loadingSettings = false;

        try
        {
            await _settingsService.SaveAsync(_appSettings);
            UpdatePowerScheduleStatusDisplay();
            StatusText.Text = _appSettings.PowerScheduleEnabled
                ? $"Daily Windows {_appSettings.PowerScheduleAction.ToLowerInvariant()} scheduler enabled."
                : "Daily Windows power scheduler disabled.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Toggle Windows power schedule", exception);
            WpfMessageBox.Show(exception.Message, "System power", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void PowerScheduleToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _loadingSettings || PowerScheduleToggle is null)
        {
            return;
        }

        _appSettings.PowerScheduleEnabled = PowerScheduleToggle.IsChecked == true;
        if (_appSettings.PowerScheduleEnabled)
        {
            _appSettings.PowerScheduleLastTriggeredDate = string.Empty;
        }

        await SavePowerScheduleSettingsAsync();
    }

    private async void PowerScheduleSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _loadingSettings ||
            PowerActionComboBox is null || PowerHourComboBox is null || PowerMinuteComboBox is null)
        {
            return;
        }

        ReadPowerScheduleControls();
        _appSettings.PowerScheduleLastTriggeredDate = string.Empty;
        await SavePowerScheduleSettingsAsync();
    }

    private async Task SavePowerScheduleSettingsAsync()
    {
        NormalizePowerScheduleSettings(_appSettings);
        try
        {
            await _settingsService.SaveAsync(_appSettings);
            UpdatePowerScheduleStatusDisplay();
            StatusText.Text = _appSettings.PowerScheduleEnabled
                ? $"Daily Windows {_appSettings.PowerScheduleAction.ToLowerInvariant()} scheduled for {_appSettings.PowerScheduleHour:00}:{_appSettings.PowerScheduleMinute:00}."
                : "Daily Windows power schedule disabled.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save Windows power schedule", exception);
        }
    }

    private async void ForcePowerActionToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _loadingSettings || ForcePowerActionToggle is null)
        {
            return;
        }

        _appSettings.ForcePowerAction = ForcePowerActionToggle.IsChecked == true;
        await SavePowerScheduleSettingsAsync();
    }

    private async void ClearTemporaryFilesButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = WpfMessageBox.Show(
            "Clear files from the current user's Temp folder and Windows Temp? Files currently in use are skipped.\n\nAfter cleanup, Windows Utility by Sajith will show every deleted/skipped item and the reclaimed space.",
            "Clear temporary files",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        ClearTemporaryFilesButton.IsEnabled = false;
        StatusText.Text = "Cleaning temporary files...";
        try
        {
            TemporaryFileCleanupResult result = await _temporaryFileCleanupService.CleanAsync();
            StatusText.Text = $"Temporary cleanup complete: {result.SizeDisplay} reclaimed, {result.FilesDeleted} file(s) deleted, {result.ItemsSkipped} item(s) skipped.";

            TemporaryFileCleanupReportWindow reportWindow = new(result) { Owner = this };
            reportWindow.ShowDialog();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Temporary file cleanup", exception);
            WpfMessageBox.Show(exception.Message, "Temporary files", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ClearTemporaryFilesButton.IsEnabled = true;
        }
    }

    private async void TerminateUserApplicationsButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = WpfMessageBox.Show(
            "Close all visible non-Windows applications in the current sign-in session?\n\nWindows Utility by Sajith and Windows system executables are excluded. Applications are asked to close normally first; if they do not exit within a short timeout, they are force terminated.\n\nUNSAVED WORK IN OTHER APPLICATIONS MAY BE LOST.",
            "Close user applications",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        TerminateUserApplicationsButton.IsEnabled = false;
        StatusText.Text = "Closing visible user applications...";
        try
        {
            UserApplicationTerminationResult result = await _userApplicationTerminationService
                .TerminateVisibleUserApplicationsAsync();

            StatusText.Text = $"Application cleanup complete: {result.ApplicationsEnded} ended, {result.Failed} failed/skipped, {result.SystemProcessesExcluded} Windows process(es) excluded.";

            UserApplicationTerminationReportWindow reportWindow = new(result) { Owner = this };
            reportWindow.ShowDialog();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Close user applications", exception);
            WpfMessageBox.Show(exception.Message, "Close user applications", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TerminateUserApplicationsButton.IsEnabled = true;
        }
    }

    private async void RescanCpuSensorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshingCpuTemperature)
        {
            return;
        }

        if (IsBackgroundServiceRunning)
        {
            try
            {
                StatusText.Text = "Rescanning interactive-session CPU sensors...";
                CpuTemperatureText.Text = "Rescanning...";
                CpuTemperatureDetailText.Text = "Checking LibreHardwareMonitor and HWiNFO session sensors";
                await Task.Run(_hardwareMonitorService.Rescan);
                await RefreshBackgroundMonitoringServiceAsync();
                await RefreshCpuTemperatureAsync();
                StatusText.Text = CpuTemperatureText.Text == "Not available"
                    ? "No CPU-specific sensor was found. If HWiNFO is used, keep Sensors-only mode running with Shared Memory Support enabled; Restart Service can rescan LocalSystem sensors."
                    : "CPU temperature sensors rescanned successfully.";
            }
            catch (Exception exception)
            {
                ApplicationLogService.WriteException("Interactive CPU sensor rescan", exception);
                WpfMessageBox.Show(exception.Message, "CPU sensor rescan", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "CPU sensor rescan failed.";
            }

            return;
        }

        try
        {
            StatusText.Text = "Rescanning CPU temperature sensors...";
            CpuTemperatureText.Text = "Rescanning...";
            CpuTemperatureDetailText.Text = "Reinitializing sensor access";
            await Task.Run(_hardwareMonitorService.Rescan);
            await RefreshCpuTemperatureAsync();
            StatusText.Text = CpuTemperatureText.Text == "Not available"
                ? "Sensor rescan completed, but no supported CPU sensor was found."
                : "CPU temperature sensors rescanned successfully.";
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(exception.Message, "CPU sensor rescan", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "CPU sensor rescan failed.";
        }
    }

    private void RestartAsAdministratorButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _windowsUtilityService.RestartApplicationAsAdministrator();
            _allowApplicationExit = true;
            WpfApplication.Current.Shutdown();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            StatusText.Text = "Administrator approval was cancelled.";
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(exception.Message, "CPU temperature", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StartSpoolerButton_Click(object sender, RoutedEventArgs e) =>
        await RunSpoolerActionAsync(_windowsUtilityService.StartPrintSpoolerAsync, "Print Spooler started.");

    private async void StopSpoolerButton_Click(object sender, RoutedEventArgs e) =>
        await RunSpoolerActionAsync(_windowsUtilityService.StopPrintSpoolerAsync, "Print Spooler stopped.");

    private async void RestartSpoolerButton_Click(object sender, RoutedEventArgs e) =>
        await RunSpoolerActionAsync(_windowsUtilityService.RestartPrintSpoolerAsync, "Print Spooler restarted.");

    private async Task RunSpoolerActionAsync(Func<Task> action, string successMessage)
    {
        try
        {
            StatusText.Text = "Sending Print Spooler command to the background service...";
            await action();
            await Task.Delay(500);
            await RefreshSpoolerStatusAsync();
            await RefreshInstalledPrintersAsync();
            StatusText.Text = successMessage;
        }
        catch (TimeoutException exception)
        {
            WpfMessageBox.Show(exception.Message, "Print Spooler", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "The background service did not respond to the Print Spooler command.";
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(exception.Message, "Print Spooler", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "The Print Spooler command failed.";
        }
    }

    private async Task RefreshCpuPackagePowerAsync()
    {
        if (_refreshingCpuPackagePower)
        {
            return;
        }

        _refreshingCpuPackagePower = true;
        try
        {
            CpuPackagePowerReading reading;
            DateTime sampleRecordedAt;
            bool usingServiceReading = IsBackgroundServiceRunning &&
                _backgroundServiceSnapshot is not null &&
                DateTime.Now - _backgroundServiceSnapshot.UpdatedAt <= TimeSpan.FromSeconds(15) &&
                _backgroundServiceSnapshot.CpuPackagePowerWatts.HasValue;

            if (usingServiceReading)
            {
                BackgroundServiceSnapshot snapshot = _backgroundServiceSnapshot!;
                double watts = snapshot.CpuPackagePowerWatts!.Value;
                reading = new CpuPackagePowerReading(
                    true,
                    watts,
                    string.IsNullOrWhiteSpace(snapshot.CpuPackagePowerDisplay)
                        ? $"{watts:0.0} W"
                        : snapshot.CpuPackagePowerDisplay,
                    snapshot.CpuPackagePowerSensorName,
                    string.IsNullOrWhiteSpace(snapshot.CpuPackagePowerSource)
                        ? "LocalSystem background service"
                        : $"{snapshot.CpuPackagePowerSource} - LocalSystem service",
                    snapshot.CpuPackagePowerDiagnostic);
                sampleRecordedAt = snapshot.CpuPackagePowerRecordedAt ?? snapshot.UpdatedAt;
            }
            else
            {
                reading = await Task.Run(_hardwareMonitorService.GetCpuPackagePowerReading);
                sampleRecordedAt = DateTime.Now;
            }

            if (reading.IsAvailable && reading.Watts.HasValue)
            {
                double watts = Math.Max(0, reading.Watts.Value);
                CpuPackagePowerText.Text = $"{watts:0.0} W";
                CpuPackagePowerSensorText.Text = string.IsNullOrWhiteSpace(reading.SensorName)
                    ? reading.Source
                    : $"{reading.SensorName} - {reading.Source}";
                CpuPackagePowerSensorText.ToolTip = string.IsNullOrWhiteSpace(reading.Diagnostic)
                    ? CpuPackagePowerSensorText.Text
                    : reading.Diagnostic;

                bool newSample = !usingServiceReading ||
                    !_lastCpuPackagePowerRecordedAt.HasValue ||
                    sampleRecordedAt > _lastCpuPackagePowerRecordedAt.Value;
                if (newSample)
                {
                    _cpuPackagePowerSessionSampleCount++;
                    _cpuPackagePowerSessionTotalWatts += watts;
                    _cpuPackagePowerSessionMaxWatts = Math.Max(_cpuPackagePowerSessionMaxWatts, watts);
                    _lastCpuPackagePowerRecordedAt = sampleRecordedAt;
                }

                if (_cpuPackagePowerSessionSampleCount > 0)
                {
                    double average = _cpuPackagePowerSessionTotalWatts / _cpuPackagePowerSessionSampleCount;
                    CpuPackagePowerAverageText.Text = $"Average this session: {average:0.0} W";
                    CpuPackagePowerMaxText.Text = $"Max this session: {_cpuPackagePowerSessionMaxWatts:0.0} W";
                }
            }
            else
            {
                CpuPackagePowerText.Text = "Not available";
                CpuPackagePowerSensorText.Text = "CPU package power sensor unavailable";
                CpuPackagePowerSensorText.ToolTip = reading.Diagnostic;
                if (_cpuPackagePowerSessionSampleCount == 0)
                {
                    CpuPackagePowerAverageText.Text = "Average this session: -- W";
                    CpuPackagePowerMaxText.Text = "Max this session: -- W";
                }
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("CPU package power display", exception);
            CpuPackagePowerText.Text = "Not available";
            CpuPackagePowerSensorText.Text = "Power sensor error";
            CpuPackagePowerSensorText.ToolTip = exception.Message;
        }
        finally
        {
            _refreshingCpuPackagePower = false;
        }
    }

    private async Task RefreshGpuPowerAsync()
    {
        if (_refreshingGpuPower)
        {
            return;
        }

        _refreshingGpuPower = true;
        try
        {
            GpuPowerReading reading;
            DateTime sampleRecordedAt;
            bool usingServiceReading = IsBackgroundServiceRunning &&
                _backgroundServiceSnapshot is not null &&
                DateTime.Now - _backgroundServiceSnapshot.UpdatedAt <= TimeSpan.FromSeconds(15) &&
                _backgroundServiceSnapshot.GpuPowerWatts.HasValue;

            if (usingServiceReading)
            {
                BackgroundServiceSnapshot snapshot = _backgroundServiceSnapshot!;
                double watts = snapshot.GpuPowerWatts!.Value;
                reading = new GpuPowerReading(
                    true,
                    watts,
                    string.IsNullOrWhiteSpace(snapshot.GpuPowerDisplay) ? $"{watts:0.0} W" : snapshot.GpuPowerDisplay,
                    snapshot.GpuPowerSensorName,
                    string.IsNullOrWhiteSpace(snapshot.GpuPowerSource)
                        ? "LocalSystem background service"
                        : $"{snapshot.GpuPowerSource} - LocalSystem service",
                    snapshot.GpuPowerDiagnostic);
                sampleRecordedAt = snapshot.GpuPowerRecordedAt ?? snapshot.UpdatedAt;
            }
            else
            {
                reading = await Task.Run(_hardwareMonitorService.GetGpuPowerReading);
                sampleRecordedAt = DateTime.Now;
            }

            if (reading.IsAvailable && reading.Watts.HasValue)
            {
                double watts = Math.Max(0, reading.Watts.Value);
                GpuPowerText.Text = $"{watts:0.0} W";
                GpuPowerSensorText.Text = string.IsNullOrWhiteSpace(reading.SensorName)
                    ? reading.Source
                    : $"{reading.SensorName} - {reading.Source}";
                GpuPowerSensorText.ToolTip = string.IsNullOrWhiteSpace(reading.Diagnostic)
                    ? GpuPowerSensorText.Text
                    : reading.Diagnostic;

                bool newSample = !usingServiceReading ||
                    !_lastGpuPowerRecordedAt.HasValue ||
                    sampleRecordedAt > _lastGpuPowerRecordedAt.Value;
                if (newSample)
                {
                    _gpuPowerSessionSampleCount++;
                    _gpuPowerSessionTotalWatts += watts;
                    _gpuPowerSessionMaxWatts = Math.Max(_gpuPowerSessionMaxWatts, watts);
                    _lastGpuPowerRecordedAt = sampleRecordedAt;
                }

                if (_gpuPowerSessionSampleCount > 0)
                {
                    double average = _gpuPowerSessionTotalWatts / _gpuPowerSessionSampleCount;
                    GpuPowerAverageText.Text = $"Average this session: {average:0.0} W";
                    GpuPowerMaxText.Text = $"Max this session: {_gpuPowerSessionMaxWatts:0.0} W";
                }
            }
            else
            {
                GpuPowerText.Text = "Not available";
                GpuPowerSensorText.Text = "GPU power sensor unavailable";
                GpuPowerSensorText.ToolTip = reading.Diagnostic;
                if (_gpuPowerSessionSampleCount == 0)
                {
                    GpuPowerAverageText.Text = "Average this session: -- W";
                    GpuPowerMaxText.Text = "Max this session: -- W";
                }
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("GPU power display", exception);
            GpuPowerText.Text = "Not available";
            GpuPowerSensorText.Text = "GPU power sensor error";
            GpuPowerSensorText.ToolTip = exception.Message;
        }
        finally
        {
            _refreshingGpuPower = false;
        }
    }

    private async Task RefreshCpuTemperatureAsync()
    {
        if (_refreshingCpuTemperature)
        {
            return;
        }

        _refreshingCpuTemperature = true;
        try
        {
            // The fan inventory and GPU sensors are read locally in both cases. They used to sit
            // after this early return, so whenever the background service supplied the CPU
            // reading they never ran and the cards stayed on their placeholder text.
            await RefreshFanAndGpuSensorsAsync();

            if (TryDisplayBackgroundServiceCpuReading())
            {
                return;
            }

            CpuTemperatureReading reading = await Task.Run(
                _hardwareMonitorService.GetCpuTemperatureReading);
            CpuFanReading fanReading = await Task.Run(
                _hardwareMonitorService.GetCpuFanReading);

            CpuFanSpeedText.Text = fanReading.IsAvailable
                ? $"CPU fan: {fanReading.Display} - {fanReading.SensorName}"
                : fanReading.Display;
            CpuFanSpeedText.ToolTip = fanReading.Diagnostic;


            bool isAdministrator = _windowsUtilityService.IsRunningAsAdministrator();
            CpuTemperatureText.Text = reading.Display;
            CpuTemperatureDetailText.Text = reading.IsAvailable && reading.IsCpuSpecific
                ? $"{reading.SensorName} - {reading.Source}"
                : "CPU sensor unavailable";

            string privilegeStatus = isAdministrator
                ? "Running with administrator sensor access."
                : "Running without administrator sensor access.";

            string serviceDiagnostic = string.Empty;
            if (IsBackgroundServiceRunning && _backgroundServiceSnapshot is not null &&
                !_backgroundServiceSnapshot.CpuIsCpuSpecific &&
                !string.IsNullOrWhiteSpace(_backgroundServiceSnapshot.CpuDiagnostic))
            {
                serviceDiagnostic = $" Background service: {_backgroundServiceSnapshot.CpuDiagnostic}";
            }

            CpuSensorHelpText.Text = $"{reading.Diagnostic}{serviceDiagnostic} {privilegeStatus}";

            bool shouldOfferElevation =
                !isAdministrator && (!reading.IsAvailable || !reading.IsCpuSpecific);

            RestartAsAdministratorButton.Visibility = shouldOfferElevation
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (reading.IsAvailable && reading.ValueCelsius.HasValue)
            {
                if (reading.IsCpuSpecific)
                {
                    await TrackCpuTemperatureAsync(
                        reading.ValueCelsius.Value,
                        $"{reading.SensorName} ({reading.Source})");
                    EvaluateTemperatureAlert(reading.ValueCelsius.Value, isCpuSpecific: true);
                }
                else
                {
                    CpuTemperatureText.Foreground = NormalCpuTemperatureBrush;
                    CpuTemperatureDetailText.Text =
                        $"{reading.SensorName} - fallback thermal zone";
                    EvaluateTemperatureAlert(null, isCpuSpecific: false);
                }
            }
            else
            {
                CpuTemperatureText.Foreground = NormalCpuTemperatureBrush;
                EvaluateTemperatureAlert(null, isCpuSpecific: false);
            }
        }
        catch (Exception exception)
        {
            CpuTemperatureText.Text = "Not available";
            CpuTemperatureDetailText.Text = "Sensor error";
            CpuFanSpeedText.Text = "Fan: Not available";
            CpuSensorHelpText.Text = $"Temperature monitoring error: {exception.Message}";
            CpuTemperatureText.Foreground = NormalCpuTemperatureBrush;
            EvaluateTemperatureAlert(null, isCpuSpecific: false);
        }
        finally
        {
            _refreshingCpuTemperature = false;
        }
    }

    private async Task TrackCpuTemperatureAsync(double temperatureCelsius, string sensorName)
    {
        bool isAboveThreshold = temperatureCelsius > CpuOverheatThresholdCelsius;
        bool hasReturnedBelowThreshold = temperatureCelsius < CpuOverheatThresholdCelsius;

        if (isAboveThreshold && _activeCpuTemperatureEvent is null)
        {
            _activeCpuTemperatureEvent = new CpuTemperatureLogEntry
            {
                StartedAt = DateTime.Now,
                StartTemperatureCelsius = temperatureCelsius,
                PeakTemperatureCelsius = temperatureCelsius,
                SensorName = sensorName
            };

            _cpuTemperatureLogs.Insert(0, _activeCpuTemperatureEvent);
            await SaveCpuTemperatureLogsAsync();
            StatusText.Text = $"CPU temperature exceeded 90 °C at {temperatureCelsius:0.0} °C. The event was recorded immediately.";
        }
        else if (_activeCpuTemperatureEvent is not null)
        {
            if (temperatureCelsius > _activeCpuTemperatureEvent.PeakTemperatureCelsius)
            {
                _activeCpuTemperatureEvent.PeakTemperatureCelsius = temperatureCelsius;
                await SaveCpuTemperatureLogsAsync();
            }

            if (hasReturnedBelowThreshold)
            {
                _activeCpuTemperatureEvent.EndedAt = DateTime.Now;
                TimeSpan duration = _activeCpuTemperatureEvent.EndedAt.Value - _activeCpuTemperatureEvent.StartedAt;
                await SaveCpuTemperatureLogsAsync();
                StatusText.Text = $"CPU temperature returned below 90 °C. Overheat duration: {FormatDuration(duration)}.";
                _activeCpuTemperatureEvent = null;
            }
        }

        bool activeOrAboveThreshold = _activeCpuTemperatureEvent is not null || isAboveThreshold;
        CpuTemperatureText.Foreground = activeOrAboveThreshold
            ? WarningCpuTemperatureBrush
            : NormalCpuTemperatureBrush;

        if (_activeCpuTemperatureEvent is not null)
        {
            TimeSpan activeDuration = DateTime.Now - _activeCpuTemperatureEvent.StartedAt;
            CpuTemperatureDetailText.Text = $"Overheat active for {FormatDuration(activeDuration)} - {sensorName}";
        }
    }

    private async Task ReloadCpuTemperatureLogsAsync()
    {
        IReadOnlyList<CpuTemperatureLogEntry> savedLogs = await _cpuTemperatureLogService.LoadAsync();
        _cpuTemperatureLogs.Clear();
        _cpuTemperatureLogs.AddRange(savedLogs);
        _activeCpuTemperatureEvent = _cpuTemperatureLogs
            .Where(entry => entry.IsActive)
            .OrderByDescending(entry => entry.StartedAt)
            .FirstOrDefault();
    }

    private async Task SaveCpuTemperatureLogsAsync()
    {
        try
        {
            await _cpuTemperatureLogService.SaveAsync(_cpuTemperatureLogs);
        }
        catch (Exception exception)
        {
            CpuSensorHelpText.Text = $"The CPU temperature event log could not be saved: {exception.Message}";
        }
    }

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

    private async Task RefreshBackgroundMonitoringServiceAsync()
    {
        if (_refreshingBackgroundService)
        {
            return;
        }

        _refreshingBackgroundService = true;
        bool wasRunning = IsBackgroundServiceRunning;
        try
        {
            _backgroundServiceInfo = await _backgroundServiceManager.GetInfoAsync();
            _backgroundServiceSnapshot = await _backgroundRuntimeStore.LoadAsync();

            BackgroundServiceStatusText.Text = _backgroundServiceInfo.Status;
            BackgroundServiceStartupText.Text = _backgroundServiceInfo.StartupType;
            BackgroundServiceAccountText.Text = _backgroundServiceInfo.Account;
            DashboardPrivilegeText.Text = _windowsUtilityService.IsRunningAsAdministrator()
                ? "Administrator"
                : "Standard user - restart required";
            DashboardPrivilegeText.Foreground = _windowsUtilityService.IsRunningAsAdministrator()
                ? WpfBrushes.ForestGreen
                : WarningCpuTemperatureBrush;
            ApplyBackgroundServiceActionButtonStates();

            bool snapshotIsFresh = _backgroundServiceSnapshot is not null &&
                DateTime.Now - _backgroundServiceSnapshot.UpdatedAt < TimeSpan.FromMinutes(2);

            BackgroundServiceLastStartedText.Text = snapshotIsFresh
                ? _backgroundServiceSnapshot!.ServiceStartedAt.ToString("dd/MM/yyyy HH:mm:ss")
                : "Not available";

            if (_backgroundServiceInfo.IsRunning && snapshotIsFresh)
            {
                ApplyBackgroundDeviceStatuses(_backgroundServiceSnapshot!);
                DisplaySystemResourceSnapshot(_backgroundServiceSnapshot!);
            }
            else
            {
                RefreshSystemResourceUsage();
            }

            if (wasRunning != IsBackgroundServiceRunning)
            {
                UpdateAutomaticMonitoringState();
            }

            UpdatePingCountdownDisplay();
            UpdatePowerScheduleStatusDisplay();
            UpdateTrayServiceStatus();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Background service status", exception);
            BackgroundServiceStatusText.Text = "Unavailable";
            BackgroundServiceStartupText.Text = "Unknown";
            BackgroundServiceAccountText.Text = "Unknown";
            BackgroundServiceLastStartedText.Text = "Not available";
            EnableBackgroundServiceButton.IsEnabled = false;
            DisableBackgroundServiceButton.IsEnabled = false;
            RestartBackgroundServiceButton.IsEnabled = false;
            DashboardPrivilegeText.Text = _windowsUtilityService.IsRunningAsAdministrator()
                ? "Administrator"
                : "Standard user - restart required";
            UpdateTrayServiceStatus();
        }
        finally
        {
            _refreshingBackgroundService = false;
        }
    }

    private void ApplyBackgroundDeviceStatuses(BackgroundServiceSnapshot snapshot)
    {
        IReadOnlyList<DeviceRuntimeStatus> runtimeStatuses = snapshot.Devices ?? new List<DeviceRuntimeStatus>();
        foreach (DeviceRuntimeStatus runtimeStatus in runtimeStatuses)
        {
            if (runtimeStatus is null)
            {
                continue;
            }
            DeviceEntry? device = Devices.FirstOrDefault(item => item.Id == runtimeStatus.DeviceId);
            if (device is null)
            {
                continue;
            }

            if (device.LastChecked.HasValue &&
                runtimeStatus.LastChecked.HasValue &&
                device.LastChecked.Value > runtimeStatus.LastChecked.Value)
            {
                continue;
            }

            device.Status = string.IsNullOrWhiteSpace(runtimeStatus.Status) ? "Not checked" : runtimeStatus.Status;
            device.Hostname = string.IsNullOrWhiteSpace(runtimeStatus.Hostname) ? "Not available" : runtimeStatus.Hostname;
            device.MacAddress = string.IsNullOrWhiteSpace(runtimeStatus.MacAddress) ? "Not available" : runtimeStatus.MacAddress;
            device.RoundTripTime = runtimeStatus.RoundTripTime;
            device.LastChecked = runtimeStatus.LastChecked;
            device.LastError = runtimeStatus.LastError ?? string.Empty;
        }

        UpdateSelectedDeviceDetails();
    }

    private void RefreshSystemResourceUsage()
    {
        bool freshServiceSnapshot = IsBackgroundServiceRunning &&
            _backgroundServiceSnapshot is not null &&
            DateTime.Now - _backgroundServiceSnapshot.UpdatedAt <= TimeSpan.FromSeconds(15);

        if (freshServiceSnapshot)
        {
            DisplaySystemResourceSnapshot(_backgroundServiceSnapshot!);
            return;
        }

        try
        {
            SystemResourceReading reading = _systemResourceMonitorService.Read();
            DisplaySystemResourceReading(reading, "Dashboard process");
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("System resource display", exception);
            CpuUsageText.Text = "--%";
            RamUsageText.Text = "--%";
            NetworkStatusText.Text = "Unavailable";
            NetworkDetailText.Text = exception.Message;
        }
    }

    private void DisplaySystemResourceSnapshot(BackgroundServiceSnapshot snapshot)
    {
        CpuUsageText.Text = snapshot.CpuUsagePercent.HasValue
            ? $"{snapshot.CpuUsagePercent.Value:0.0}%"
            : "--%";
        bool cpuHigh = snapshot.CpuUsagePercent.HasValue && snapshot.CpuUsagePercent.Value >= CpuHighUsageThresholdPercent;
        string cpuModel = string.IsNullOrWhiteSpace(snapshot.CpuModel) ? "CPU model unavailable" : snapshot.CpuModel;
        CpuUsageDetailText.Text = cpuHigh
            ? $"High - {cpuModel}"
            : cpuModel;
        CpuUsageText.Foreground = cpuHigh
            ? WarningCpuTemperatureBrush
            : NormalCpuTemperatureBrush;

        RamUsageText.Text = $"{snapshot.RamUsagePercent:0.0}%";
        ulong usedMemory = snapshot.TotalPhysicalMemoryBytes >= snapshot.AvailablePhysicalMemoryBytes
            ? snapshot.TotalPhysicalMemoryBytes - snapshot.AvailablePhysicalMemoryBytes
            : 0;
        string ramCapacity = snapshot.TotalPhysicalMemoryBytes > 0
            ? $"{FormatBytesAsGb(usedMemory)} / {FormatBytesAsGb(snapshot.TotalPhysicalMemoryBytes)}"
            : "Physical RAM";
        string ramSpeed = string.IsNullOrWhiteSpace(snapshot.RamSpeedDisplay) ? "RAM speed unavailable" : snapshot.RamSpeedDisplay;
        RamUsageDetailText.Text = $"{ramCapacity} - {ramSpeed}";
        RamUsageText.Foreground = snapshot.RamUsagePercent > RamHighUsageThresholdPercent
            ? WarningCpuTemperatureBrush
            : NormalCpuTemperatureBrush;

        _lastNetworkConnected = snapshot.NetworkConnected;
        _lastMemoryUsagePercent = snapshot.RamUsagePercent;

        NetworkStatusText.Text = snapshot.NetworkConnected ? "Connected" : "Disconnected";
        NetworkStatusText.Foreground = snapshot.NetworkConnected
            ? WpfBrushes.SeaGreen
            : WarningCpuTemperatureBrush;
        NetworkDetailText.Text = string.IsNullOrWhiteSpace(snapshot.NetworkSummary)
            ? "No active Ethernet or Wi-Fi connection detected."
            : snapshot.NetworkSummary;
    }

    private void DisplaySystemResourceReading(SystemResourceReading reading, string source)
    {
        CpuUsageText.Text = reading.CpuUsagePercent.HasValue
            ? $"{reading.CpuUsagePercent.Value:0.0}%"
            : "--%";
        bool cpuHigh = reading.CpuUsagePercent.HasValue && reading.CpuUsagePercent.Value >= CpuHighUsageThresholdPercent;
        CpuUsageDetailText.Text = cpuHigh
            ? $"High - {reading.CpuModel}"
            : reading.CpuModel;
        CpuUsageText.Foreground = cpuHigh
            ? WarningCpuTemperatureBrush
            : NormalCpuTemperatureBrush;

        RamUsageText.Text = $"{reading.RamUsagePercent:0.0}%";
        ulong usedMemory = reading.TotalPhysicalMemoryBytes >= reading.AvailablePhysicalMemoryBytes
            ? reading.TotalPhysicalMemoryBytes - reading.AvailablePhysicalMemoryBytes
            : 0;
        string ramCapacity = reading.TotalPhysicalMemoryBytes > 0
            ? $"{FormatBytesAsGb(usedMemory)} / {FormatBytesAsGb(reading.TotalPhysicalMemoryBytes)}"
            : "Physical RAM";
        RamUsageDetailText.Text = $"{ramCapacity} - {reading.RamSpeedDisplay}";
        RamUsageText.Foreground = reading.RamUsagePercent > RamHighUsageThresholdPercent
            ? WarningCpuTemperatureBrush
            : NormalCpuTemperatureBrush;

        _lastNetworkConnected = reading.NetworkConnected;
        _lastNetworkSummary = reading.NetworkSummary;
        _lastMemoryUsagePercent = reading.RamUsagePercent;

        NetworkStatusText.Text = reading.NetworkConnected ? "Connected" : "Disconnected";
        NetworkStatusText.Foreground = reading.NetworkConnected
            ? WpfBrushes.SeaGreen
            : WarningCpuTemperatureBrush;
        NetworkDetailText.Text = reading.NetworkSummary;
    }

    private static string FormatBytesAsGb(ulong bytes) =>
        $"{bytes / 1024d / 1024d / 1024d:0.0} GB";

    private async Task RefreshSystemHardwareInventoryAsync()
    {
        if (_refreshingSystemHardwareInventory)
        {
            return;
        }

        _refreshingSystemHardwareInventory = true;
        try
        {
            HardwareInventoryUpdatedText.Text = "Refreshing hardware inventory...";
            SystemHardwareInventory inventory = await Task.Run(_systemHardwareInventoryService.Read);

            PcNameText.Text = inventory.ComputerName;
            PcBrandText.Text = inventory.Manufacturer;
            PcModelText.Text = inventory.Model;
            PcSkuText.Text = inventory.SystemSku;
            PcSerialText.Text = inventory.SerialNumber;

            int moduleCount = inventory.MemoryModules.Count;
            int slotCount = Math.Max(moduleCount, inventory.MemorySlots);
            RamModuleCountText.Text = slotCount > 0
                ? $"Modules: {moduleCount} installed / {slotCount} slot(s)"
                : $"Modules: {moduleCount} installed";
            RamModuleSummaryText.Text = slotCount > 0
                ? $"{moduleCount} module(s) installed across {slotCount} reported slot(s)."
                : $"{moduleCount} memory module(s) detected.";
            RamModuleInventoryList.ItemsSource = inventory.MemoryModules.Count > 0
                ? inventory.MemoryModules.Select(module => module.Display).ToArray()
                : ["RAM module details were not reported by the firmware."];

            DisplayInventorySummaryText.Text = $"{inventory.Displays.Count} connected display(s) detected.";
            DisplayInventoryList.ItemsSource = inventory.Displays.Count > 0
                ? inventory.Displays.Select(display => display.Display).ToArray()
                : ["Display details unavailable."];

            GpuInventorySummaryText.Text = $"{inventory.GraphicsAdapters.Count} graphics adapter(s) detected.";
            GpuInventoryList.ItemsSource = inventory.GraphicsAdapters.Count > 0
                ? inventory.GraphicsAdapters.Select(gpu => gpu.Display).ToArray()
                : ["Graphics adapter details unavailable."];

            StorageInventorySummaryText.Text =
                $"{inventory.PhysicalDisks.Count} physical disk(s) • {inventory.Volumes.Count} mounted local/removable volume(s).";
            PhysicalDiskInventoryList.ItemsSource = inventory.PhysicalDisks.Count > 0
                ? inventory.PhysicalDisks.Select(disk => disk.Display).ToArray()
                : ["Physical disk details unavailable."];
            StorageVolumeInventoryList.ItemsSource = inventory.Volumes.Count > 0
                ? inventory.Volumes.Select(volume => volume.Display).ToArray()
                : ["No ready local/removable volumes were detected."];

            HardwareInventoryUpdatedText.Text = $"Hardware inventory updated {inventory.UpdatedAt:HH:mm:ss}.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("System hardware inventory display", exception);
            HardwareInventoryUpdatedText.Text = "Hardware inventory unavailable.";
        }
        finally
        {
            _refreshingSystemHardwareInventory = false;
        }
    }

    private async Task RefreshSystemHealthDetailsAsync(bool forcePublicRefresh = false)
    {
        if (_refreshingSystemHealthDetails)
        {
            return;
        }

        _refreshingSystemHealthDetails = true;
        try
        {
            SystemHealthDetails details = await _systemHealthDetailsService.GetAsync(forcePublicRefresh);
            WindowsVersionText.Text = details.WindowsDisplay;
            BatteryPercentText.Text = details.BatteryDisplay;
            WifiStrengthText.Text = details.WifiDisplay;
            LocalIpText.Text = details.LocalIpDisplay;
            GatewayDnsText.Text = details.GatewayDnsDisplay;
            PublicIpText.Text = details.PublicIpDisplay;
            PublicLocationText.Text = details.PublicLocationDisplay;
            PublicServerText.Text = details.PublicServerDisplay;

            NetworkLinkSnapshot link = await _networkSpeedTestService.GetActiveLinkAsync(
                checkInternet: false,
                measureGatewayLatency: false,
                measureCurrentTraffic: false);
            ActiveLinkSpeedText.Text = link.CompactLinkDisplay;

            SystemHealthUpdatedText.Text = $"Updated {details.UpdatedAt:HH:mm:ss}";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("System health details", exception);
            SystemHealthUpdatedText.Text = "Extended health details unavailable";
        }
        finally
        {
            _refreshingSystemHealthDetails = false;
        }
    }

    private async void RefreshSystemHealthDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        SystemHealthUpdatedText.Text = "Refreshing...";
        await RefreshSystemHealthDetailsAsync(forcePublicRefresh: true);
    }

    private void OpenNetworkSpeedTestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            NetworkSpeedTestWindow window = new()
            {
                Owner = this
            };
            window.ShowDialog();
            StatusText.Text = "Closed Network Speed & Bandwidth.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Open Network Speed & Bandwidth", exception);
            WpfMessageBox.Show(this, exception.Message, "Network Speed & Bandwidth", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ViewSystemHealthLogButton_Click(object sender, RoutedEventArgs e)
    {
        SystemHealthLogWindow window = new(_systemHealthLogService)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private bool TryDisplayBackgroundServiceCpuReading()
    {
        if (!IsBackgroundServiceRunning ||
            _backgroundServiceSnapshot is null ||
            DateTime.Now - _backgroundServiceSnapshot.UpdatedAt > TimeSpan.FromSeconds(15))
        {
            return false;
        }

        BackgroundServiceSnapshot snapshot = _backgroundServiceSnapshot;

        // A generic ACPI thermal zone is not a CPU package sensor. If the service
        // does not have a CPU-specific reading, allow this signed-in dashboard
        // process to try its own LibreHardwareMonitor/HWiNFO session instead.
        if (!snapshot.CpuIsAvailable ||
            !snapshot.CpuIsCpuSpecific ||
            !snapshot.CpuTemperatureCelsius.HasValue)
        {
            return false;
        }

        CpuTemperatureText.Text = snapshot.CpuDisplay;
        CpuTemperatureDetailText.Text = $"{snapshot.CpuSensorName} - {snapshot.CpuSource} - LocalSystem service";
        CpuFanSpeedText.Text = snapshot.CpuFanRpm.HasValue
            ? $"{snapshot.CpuFanDisplay} - {snapshot.CpuFanSensorName}"
            : string.IsNullOrWhiteSpace(snapshot.CpuFanDisplay) ? "Fan: RPM unavailable" : snapshot.CpuFanDisplay;
        CpuFanSpeedText.ToolTip = string.IsNullOrWhiteSpace(snapshot.CpuFanDiagnostic)
            ? "Fan RPM is not exposed by the hardware monitor."
            : snapshot.CpuFanDiagnostic;
        CpuSensorHelpText.Text = string.IsNullOrWhiteSpace(snapshot.CpuDiagnostic)
            ? "Temperature monitoring is running in the LocalSystem background service."
            : $"{snapshot.CpuDiagnostic} Monitoring is running in the LocalSystem background service.";
        RestartAsAdministratorButton.Visibility = Visibility.Collapsed;

        bool isHot = snapshot.CpuTemperatureCelsius.Value > CpuOverheatThresholdCelsius;
        CpuTemperatureText.Foreground = isHot
            ? WarningCpuTemperatureBrush
            : NormalCpuTemperatureBrush;
        EvaluateTemperatureAlert(snapshot.CpuTemperatureCelsius, isCpuSpecific: true);
        return true;
    }

    private async void EnableBackgroundServiceButton_Click(object sender, RoutedEventArgs e)
    {
        SetBackgroundServiceActionButtonsEnabled(false);
        try
        {
            StatusText.Text = "Enabling and starting the background monitoring service...";
            await _backgroundServiceManager.EnableAndStartAsync();
            await Task.Delay(1200);
            await RefreshBackgroundMonitoringServiceAsync();
            await RefreshCpuPackagePowerAsync();
            await RefreshCpuTemperatureAsync();
            StatusText.Text = "Background monitoring enabled and started. It will use Automatic (Delayed Start) at Windows startup.";
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            StatusText.Text = "Administrator approval was cancelled.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Enable background service", exception);
            WpfMessageBox.Show(exception.Message, "Background Monitoring Service", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Background monitoring service could not be enabled.";
        }
        finally
        {
            ApplyBackgroundServiceActionButtonStates();
        }
    }

    private async void DisableBackgroundServiceButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirmation = WpfMessageBox.Show(
            this,
            "Disable and stop the Background Monitoring service?\n\nThis pauses unattended managed-device checks, background CPU/sensor alerts and the system power scheduler. Dashboard-only readings can continue while the application is open. Your saved settings and schedules are kept, and you can enable the service again here.",
            "Disable Background Monitoring",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        SetBackgroundServiceActionButtonsEnabled(false);
        try
        {
            StatusText.Text = "Stopping and disabling the background monitoring service...";
            await _backgroundServiceManager.DisableAndStopAsync();
            await Task.Delay(900);
            await RefreshBackgroundMonitoringServiceAsync();
            StatusText.Text = "Background monitoring stopped and disabled. Saved settings were retained.";
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            StatusText.Text = "Administrator approval was cancelled.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Disable background service", exception);
            WpfMessageBox.Show(exception.Message, "Background Monitoring Service", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Background monitoring service could not be disabled.";
        }
        finally
        {
            ApplyBackgroundServiceActionButtonStates();
        }
    }

    private async void RestartBackgroundServiceButton_Click(object sender, RoutedEventArgs e)
    {
        SetBackgroundServiceActionButtonsEnabled(false);
        try
        {
            StatusText.Text = "Restarting the background monitoring service...";
            await _backgroundServiceManager.RestartAsync();
            await Task.Delay(1200);
            await RefreshBackgroundMonitoringServiceAsync();
            await RefreshCpuPackagePowerAsync();
            await RefreshCpuTemperatureAsync();
            StatusText.Text = "Background monitoring service restarted.";
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            StatusText.Text = "Administrator approval was cancelled.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Restart background service", exception);
            WpfMessageBox.Show(exception.Message, "Background Monitoring Service", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Background monitoring service restart failed.";
        }
        finally
        {
            ApplyBackgroundServiceActionButtonStates();
        }
    }

    private void SetBackgroundServiceActionButtonsEnabled(bool enabled)
    {
        EnableBackgroundServiceButton.IsEnabled = enabled;
        DisableBackgroundServiceButton.IsEnabled = enabled;
        RestartBackgroundServiceButton.IsEnabled = enabled;
    }

    private void ApplyBackgroundServiceActionButtonStates()
    {
        bool exists = _backgroundServiceInfo?.Exists == true;
        bool running = _backgroundServiceInfo?.IsRunning == true;
        bool disabled = string.Equals(_backgroundServiceInfo?.StartupType, "Disabled", StringComparison.OrdinalIgnoreCase);
        bool automaticDelayed = string.Equals(
            _backgroundServiceInfo?.StartupType,
            "Automatic (Delayed Start)",
            StringComparison.OrdinalIgnoreCase);

        EnableBackgroundServiceButton.IsEnabled = exists && (!running || !automaticDelayed);
        DisableBackgroundServiceButton.IsEnabled = exists && (running || !disabled);
        RestartBackgroundServiceButton.IsEnabled = exists && running;
    }

    private void ViewBackgroundServiceLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _backgroundServiceManager.OpenServiceLog();
            StatusText.Text = "Opened the background service log.";
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(exception.Message, "Background Monitoring Service", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshSpoolerStatusAsync()
    {
        if (_refreshingSpoolerStatus)
        {
            return;
        }

        _refreshingSpoolerStatus = true;
        try
        {
            string status = await _windowsUtilityService.GetPrintSpoolerStatusAsync();
            SpoolerStatusHeaderText.Text = status;

            // Printer lists change rarely; re-read them on a spooler state change or every 30 seconds.
            bool statusChanged = !string.Equals(status, _lastSpoolerStatus, StringComparison.Ordinal);
            _lastSpoolerStatus = status;
            if (statusChanged || DateTime.UtcNow - _installedPrintersReadAtUtc > TimeSpan.FromSeconds(30))
            {
                _ = RefreshInstalledPrintersAsync();
            }
        }
        finally
        {
            _refreshingSpoolerStatus = false;
        }
    }

    private async Task RefreshInstalledPrintersAsync()
    {
        if (_refreshingPrinters)
        {
            return;
        }

        _refreshingPrinters = true;
        _installedPrintersReadAtUtc = DateTime.UtcNow;
        try
        {
            if (!string.Equals(_lastSpoolerStatus, "Running", StringComparison.Ordinal))
            {
                _installedPrinters = Array.Empty<InstalledPrinterInfo>();
                InstalledPrintersList.ItemsSource = null;
                InstalledPrintersHeaderText.Text = "Installed printers";
                InstalledPrintersEmptyText.Text = "Start the Print Spooler to list installed printers.";
                InstalledPrintersEmptyText.Visibility = Visibility.Visible;
                return;
            }

            _installedPrinters = await _printerService.GetInstalledPrintersAsync();
            InstalledPrintersHeaderText.Text = $"Installed printers ({_installedPrinters.Count})";
            InstalledPrintersList.ItemsSource = _installedPrinters
                .Select(printer => new PrinterListItem(
                    printer.IsDefault ? $"★ {printer.Name}" : printer.Name,
                    DescribeInstalledPrinter(printer)))
                .ToList();
            InstalledPrintersEmptyText.Text = "No printers are installed.";
            InstalledPrintersEmptyText.Visibility = _installedPrinters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Installed printers", exception);
            InstalledPrintersEmptyText.Text = "Windows did not return the printer list.";
            InstalledPrintersEmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            _refreshingPrinters = false;
        }
    }

    private static string DescribeInstalledPrinter(InstalledPrinterInfo printer)
    {
        List<string> parts = new() { printer.Status };
        if (printer.IsDefault)
        {
            parts.Add("Default");
        }

        parts.Add(printer.IsVirtual ? "Virtual" : printer.IsNetwork ? "Network" : "Local");
        if (!string.IsNullOrWhiteSpace(printer.Address))
        {
            parts.Add(printer.Address);
        }
        else if (!string.IsNullOrWhiteSpace(printer.DeviceName))
        {
            parts.Add($"WSD {printer.DeviceName}");
        }
        else if (!printer.IsVirtual && !string.IsNullOrWhiteSpace(printer.PortName))
        {
            parts.Add(printer.PortName);
        }

        return string.Join(" • ", parts);
    }

    private async void ScanNetworkPrintersButton_Click(object sender, RoutedEventArgs e) => await ScanNetworkPrintersAsync();

    private async Task ScanNetworkPrintersAfterStartupAsync()
    {
        // Let the dashboard finish loading before probing the subnet.
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (IsLoaded)
        {
            await ScanNetworkPrintersAsync();
        }
    }

    private async Task ScanNetworkPrintersAsync()
    {
        if (_scanningNetworkPrinters)
        {
            return;
        }

        _scanningNetworkPrinters = true;
        ScanNetworkPrintersButton.IsEnabled = false;
        ScanNetworkPrintersButton.Content = "Scanning...";
        NetworkPrintersEmptyText.Text = "Looking for printers on the local network (RAW 9100, IPP 631, LPD 515)...";
        NetworkPrintersEmptyText.Visibility = Visibility.Visible;
        try
        {
            if (_installedPrintersReadAtUtc == DateTime.MinValue)
            {
                await RefreshInstalledPrintersAsync();
            }

            IReadOnlyList<NetworkPrinterInfo> printers = await _printerService.ScanNetworkPrintersAsync(_installedPrinters);
            int notInstalled = printers.Count(printer => !printer.IsInstalled);
            NetworkPrintersHeaderText.Text = printers.Count == 0
                ? "Printers on the network"
                : $"Printers on the network ({printers.Count}, {notInstalled} not installed)";
            NetworkPrintersList.ItemsSource = printers
                .Select(printer => new PrinterListItem(
                    string.IsNullOrEmpty(printer.HostName) ? printer.Address : $"{printer.HostName}",
                    string.Join(" • ", new[]
                    {
                        printer.IsInstalled ? "Installed" : "Not installed",
                        string.IsNullOrEmpty(printer.HostName) ? null : printer.Address,
                        string.Join(", ", printer.Services)
                    }.Where(part => !string.IsNullOrEmpty(part)))))
                .ToList();
            NetworkPrintersEmptyText.Text = $"No printers answered on the local network. Last scan {DateTime.Now:t}.";
            NetworkPrintersEmptyText.Visibility = printers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"Network printer scan finished: {printers.Count} found.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Network printer scan", exception);
            NetworkPrintersEmptyText.Text = $"Network scan failed: {exception.Message}";
            NetworkPrintersEmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            ScanNetworkPrintersButton.Content = "Scan network";
            ScanNetworkPrintersButton.IsEnabled = true;
            _scanningNetworkPrinters = false;
        }
    }

    private async void CpuTemperatureAlertSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // WPF can raise Slider.ValueChanged while InitializeComponent is still
        // constructing controls that appear later in XAML. On some machines this
        // happened before CpuTemperatureAlertValueText/StatusText existed and
        // caused a NullReferenceException during dashboard startup.
        if (!IsInitialized || CpuTemperatureAlertValueText is null)
        {
            return;
        }

        double threshold = NormalizeCpuAlertThreshold(e.NewValue);
        CpuTemperatureAlertValueText.Text = $"{threshold:0} °C";
        if (_loadingSettings || StatusText is null)
        {
            return;
        }

        _appSettings.CpuTemperatureAlertThresholdCelsius = threshold;
        _lastTemperatureAlertAt = null;
        try
        {
            await _settingsService.SaveAsync(_appSettings);
            StatusText.Text = $"CPU temperature sound-alert threshold set to {threshold:0} °C.";
            EvaluateTemperatureAlert(_backgroundServiceSnapshot?.CpuTemperatureCelsius, _backgroundServiceSnapshot?.CpuIsCpuSpecific == true);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save CPU alert threshold", exception);
        }
    }

    private async void CpuTemperatureSoundAlertCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _loadingSettings || CpuTemperatureSoundAlertCheckBox is null || StatusText is null)
        {
            return;
        }

        _appSettings.CpuTemperatureSoundAlertEnabled = CpuTemperatureSoundAlertCheckBox.IsChecked == true;
        try
        {
            await _settingsService.SaveAsync(_appSettings);
            StatusText.Text = _appSettings.CpuTemperatureSoundAlertEnabled
                ? "CPU temperature sound alerts enabled."
                : "CPU temperature sound alerts disabled.";

            if (_appSettings.CpuTemperatureSoundAlertEnabled)
            {
                _temperatureAlertActive = false;
                _lastTemperatureAlertAt = null;
                EvaluateTemperatureAlert(
                    _backgroundServiceSnapshot?.CpuTemperatureCelsius,
                    _backgroundServiceSnapshot?.CpuIsCpuSpecific == true);
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save CPU sound alert setting", exception);
        }
    }

    private void EvaluateTemperatureAlert(double? temperatureCelsius, bool isCpuSpecific)
    {
        double threshold = NormalizeCpuAlertThreshold(_appSettings.CpuTemperatureAlertThresholdCelsius);
        int alertIntervalSeconds = NormalizeCpuAlertInterval(_appSettings.CpuTemperatureAlertIntervalSeconds);
        bool alertActive = isCpuSpecific &&
            temperatureCelsius.HasValue &&
            temperatureCelsius.Value > threshold;

        if (!alertActive)
        {
            _temperatureAlertActive = false;
            _lastTemperatureAlertAt = null;
            return;
        }

        DateTime now = DateTime.Now;
        bool intervalElapsed = !_lastTemperatureAlertAt.HasValue ||
            now - _lastTemperatureAlertAt.Value >= TimeSpan.FromSeconds(alertIntervalSeconds);
        bool shouldAlert = !_temperatureAlertActive || intervalElapsed;

        if (shouldAlert)
        {
            string intervalText = FormatCpuAlertInterval(alertIntervalSeconds);
            string message = $"CPU temperature is {temperatureCelsius!.Value:0.0} °C, above the configured {threshold:0} °C alert threshold. Warning repeats every {intervalText} while the temperature remains high.";
            StatusText.Text = message;

            if (_appSettings.CpuTemperatureSoundAlertEnabled)
            {
                try
                {
                    SystemSounds.Exclamation.Play();
                }
                catch
                {
                }
            }

            try
            {
                if (_appSettings.DesktopNotificationsEnabled)
                {
                    _trayIcon?.ShowBalloonTip(
                        8000,
                        "CPU temperature alert",
                        message,
                        System.Windows.Forms.ToolTipIcon.Warning);
                }
            }
            catch
            {
            }

            _lastTemperatureAlertAt = now;
        }

        _temperatureAlertActive = true;
    }

    private async void CpuTemperatureAlertIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _loadingSettings || CpuTemperatureAlertIntervalComboBox is null || StatusText is null ||
            CpuTemperatureAlertIntervalComboBox.SelectedItem is not ComboBoxItem selectedItem ||
            !int.TryParse(selectedItem.Tag?.ToString(), out int seconds))
        {
            return;
        }

        seconds = NormalizeCpuAlertInterval(seconds);
        _appSettings.CpuTemperatureAlertIntervalSeconds = seconds;
        _lastTemperatureAlertAt = null;

        try
        {
            await _settingsService.SaveAsync(_appSettings);
            StatusText.Text = $"CPU temperature warning interval set to {FormatCpuAlertInterval(seconds)}.";
            EvaluateTemperatureAlert(
                _backgroundServiceSnapshot?.CpuTemperatureCelsius,
                _backgroundServiceSnapshot?.CpuIsCpuSpecific == true);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save CPU alert interval", exception);
        }
    }

    private async void AutomaticMonitoringCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _loadingSettings || AutomaticMonitoringCheckBox is null || StatusText is null)
        {
            return;
        }

        _appSettings.AutomaticMonitoringEnabled = AutomaticMonitoringCheckBox.IsChecked == true;
        UpdateAutomaticMonitoringState();

        try
        {
            await _settingsService.SaveAsync(_appSettings);
            StatusText.Text = _appSettings.AutomaticMonitoringEnabled
                ? $"Automatic ping monitoring enabled every {FormatPingInterval(_appSettings.PingIntervalSeconds)}."
                : "Automatic ping monitoring disabled.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save monitoring setting", exception);
            WpfMessageBox.Show(exception.Message, "Monitoring settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void PingIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _loadingSettings || PingIntervalComboBox is null || StatusText is null ||
            PingIntervalComboBox.SelectedItem is not ComboBoxItem selectedItem)
        {
            return;
        }

        if (!int.TryParse(selectedItem.Tag?.ToString(), out int seconds))
        {
            return;
        }

        seconds = NormalizePingInterval(seconds);
        _appSettings.PingIntervalSeconds = seconds;
        UpdateAutomaticMonitoringState();

        try
        {
            await _settingsService.SaveAsync(_appSettings);
            StatusText.Text = $"Automatic ping interval changed to {FormatPingInterval(seconds)}.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save ping interval", exception);
            WpfMessageBox.Show(exception.Message, "Ping interval", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeviceMonitorTimer_Tick(object? sender, EventArgs e)
    {
        _deviceMonitorTimer.Stop();
        _nextAutomaticPingAt = null;
        UpdatePingCountdownDisplay();

        try
        {
            await RefreshAllDevicesAsync();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Automatic ping cycle", exception);
            StatusText.Text = $"Automatic ping cycle failed: {exception.Message}";
        }
        finally
        {
            if (AutomaticMonitoringCheckBox.IsChecked == true && !IsBackgroundServiceRunning)
            {
                ScheduleNextAutomaticPing();
            }
        }
    }

    private void UpdateAutomaticMonitoringState()
    {
        _deviceMonitorTimer.Stop();
        _nextAutomaticPingAt = null;

        if (IsBackgroundServiceRunning)
        {
            UpdatePingCountdownDisplay();
            return;
        }

        if (AutomaticMonitoringCheckBox.IsChecked == true)
        {
            ScheduleNextAutomaticPing();
        }
        else
        {
            UpdatePingCountdownDisplay();
        }
    }

    private void ScheduleNextAutomaticPing()
    {
        _deviceMonitorTimer.Stop();
        int seconds = NormalizePingInterval(_appSettings.PingIntervalSeconds);
        _appSettings.PingIntervalSeconds = seconds;
        _deviceMonitorTimer.Interval = TimeSpan.FromSeconds(seconds);
        _nextAutomaticPingAt = DateTime.Now.AddSeconds(seconds);
        _deviceMonitorTimer.Start();
        UpdatePingCountdownDisplay();
    }

    private void UpdatePingCountdownDisplay()
    {
        if (AutomaticMonitoringCheckBox.IsChecked != true)
        {
            PingCountdownText.Text = "Next ping: Off";
            return;
        }

        if (IsBackgroundServiceRunning)
        {
            if (_backgroundServiceSnapshot is null ||
                DateTime.Now - _backgroundServiceSnapshot.UpdatedAt > TimeSpan.FromSeconds(15))
            {
                PingCountdownText.Text = "Service: syncing";
                return;
            }

            if (_backgroundServiceSnapshot.PingCycleRunning)
            {
                PingCountdownText.Text = "Ping: Running";
                return;
            }

            if (!_backgroundServiceSnapshot.NextPingAt.HasValue)
            {
                PingCountdownText.Text = _backgroundServiceSnapshot.MonitoringEnabled
                    ? "Next ping: --:--"
                    : "Next ping: Off";
                return;
            }

            SetPingCountdownFromTarget(_backgroundServiceSnapshot.NextPingAt.Value);
            return;
        }

        if (_refreshingDevices)
        {
            PingCountdownText.Text = "Ping: Running";
            return;
        }

        if (!_nextAutomaticPingAt.HasValue)
        {
            PingCountdownText.Text = "Next ping: --:--";
            return;
        }

        SetPingCountdownFromTarget(_nextAutomaticPingAt.Value);
    }

    private void SetPingCountdownFromTarget(DateTime target)
    {
        TimeSpan remaining = target - DateTime.Now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        string formatted = remaining.TotalHours >= 1
            ? remaining.ToString(@"hh\:mm\:ss")
            : remaining.ToString(@"mm\:ss");
        PingCountdownText.Text = $"Next ping: {formatted}";
    }

    private void SelectPingIntervalItem(int seconds)
    {
        foreach (object item in PingIntervalComboBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem &&
                int.TryParse(comboBoxItem.Tag?.ToString(), out int itemSeconds) &&
                itemSeconds == seconds)
            {
                PingIntervalComboBox.SelectedItem = comboBoxItem;
                return;
            }
        }

        // Older or custom settings are normalized to the closest supported preset.
        int closest = new[] { 10, 15, 30, 60, 120, 300, 600, 1800 }
            .OrderBy(value => Math.Abs(value - seconds))
            .First();
        _appSettings.PingIntervalSeconds = closest;
        SelectPingIntervalItem(closest);
    }

    private void PopulatePowerTimeSelectors()
    {
        if (PowerHourComboBox is null || PowerMinuteComboBox is null)
        {
            return;
        }

        for (int hour = 0; hour < 24; hour++)
        {
            PowerHourComboBox.Items.Add(new ComboBoxItem
            {
                Content = hour.ToString("00"),
                Tag = hour
            });
        }

        for (int minute = 0; minute < 60; minute++)
        {
            PowerMinuteComboBox.Items.Add(new ComboBoxItem
            {
                Content = minute.ToString("00"),
                Tag = minute
            });
        }
    }

    private void SelectPowerScheduleControls()
    {
        NormalizePowerScheduleSettings(_appSettings);

        foreach (object item in PowerActionComboBox.Items)
        {
            if (item is ComboBoxItem combo &&
                string.Equals(combo.Tag?.ToString(), _appSettings.PowerScheduleAction, StringComparison.OrdinalIgnoreCase))
            {
                PowerActionComboBox.SelectedItem = combo;
                break;
            }
        }

        SelectNumericComboItem(PowerHourComboBox, _appSettings.PowerScheduleHour);
        SelectNumericComboItem(PowerMinuteComboBox, _appSettings.PowerScheduleMinute);
    }

    private static void SelectNumericComboItem(WpfComboBox comboBox, int value)
    {
        foreach (object item in comboBox.Items)
        {
            if (item is ComboBoxItem combo &&
                int.TryParse(combo.Tag?.ToString(), out int itemValue) &&
                itemValue == value)
            {
                comboBox.SelectedItem = combo;
                return;
            }
        }
    }

    private void ReadPowerScheduleControls()
    {
        if (PowerActionComboBox.SelectedItem is ComboBoxItem actionItem)
        {
            _appSettings.PowerScheduleAction = string.Equals(
                actionItem.Tag?.ToString(),
                "Shutdown",
                StringComparison.OrdinalIgnoreCase)
                ? "Shutdown"
                : "Restart";
        }

        if (PowerHourComboBox.SelectedItem is ComboBoxItem hourItem &&
            int.TryParse(hourItem.Tag?.ToString(), out int hour))
        {
            _appSettings.PowerScheduleHour = hour;
        }

        if (PowerMinuteComboBox.SelectedItem is ComboBoxItem minuteItem &&
            int.TryParse(minuteItem.Tag?.ToString(), out int minute))
        {
            _appSettings.PowerScheduleMinute = minute;
        }
    }

    private static void NormalizePowerScheduleSettings(AppSettings settings)
    {
        settings.PowerScheduleHour = Math.Clamp(settings.PowerScheduleHour, 0, 23);
        settings.PowerScheduleMinute = Math.Clamp(settings.PowerScheduleMinute, 0, 59);
        settings.PowerScheduleAction = string.Equals(
            settings.PowerScheduleAction,
            "Shutdown",
            StringComparison.OrdinalIgnoreCase)
            ? "Shutdown"
            : "Restart";
    }

    private void UpdatePowerScheduleStatusDisplay()
    {
        if (PowerScheduleStatusText is null ||
            PowerScheduleEnabledStateText is null ||
            PowerScheduleCountdownText is null ||
            PowerScheduleToggle is null ||
            PowerScheduleControlButton is null)
        {
            return;
        }

        bool enabled = _appSettings.PowerScheduleEnabled;
        PowerScheduleToggle.Content = enabled ? "Power scheduler enabled" : "Power scheduler disabled";
        PowerScheduleControlButton.Content = enabled ? "Disable scheduler" : "Enable scheduler";
        PowerScheduleControlButton.Tag = enabled ? "\uE711" : "\uE768";
        PowerScheduleEnabledStateText.Text = enabled ? "Enabled" : "Disabled";
        PowerScheduleEnabledStateText.Foreground = enabled
            ? PowerScheduleEnabledBrush
            : PowerScheduleDisabledBrush;

        if (!enabled)
        {
            PowerScheduleCountdownText.Text = "--:--:--";
            PowerScheduleStatusText.Text = "Automatic Windows shutdown/restart is disabled.";
            return;
        }

        if (!IsBackgroundServiceRunning)
        {
            DateTime configuredClock = DateTime.Today
                .AddHours(_appSettings.PowerScheduleHour)
                .AddMinutes(_appSettings.PowerScheduleMinute);
            string configuredClock12 = configuredClock.ToString("h:mm tt", CultureInfo.InvariantCulture);
            PowerScheduleCountdownText.Text = "Paused";
            PowerScheduleStatusText.Text =
                $"Daily {_appSettings.PowerScheduleAction.ToLowerInvariant()} at {_appSettings.PowerScheduleHour:00}:{_appSettings.PowerScheduleMinute:00} ({configuredClock12}) is enabled, but the Background Monitoring service is not running. Enable the service for unattended execution.";
            return;
        }

        DateTime? next = null;
        if (IsBackgroundServiceRunning &&
            _backgroundServiceSnapshot is not null &&
            _backgroundServiceSnapshot.PowerScheduleEnabled)
        {
            next = _backgroundServiceSnapshot.NextPowerActionAt;
        }

        next ??= CalculateNextPowerActionForDisplay();

        if (next.HasValue && next.Value <= DateTime.Now)
        {
            next = CalculateNextPowerActionForDisplay();
        }

        if (next.HasValue)
        {
            TimeSpan remaining = next.Value - DateTime.Now;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            PowerScheduleCountdownText.Text = remaining.TotalDays >= 1
                ? $"{(int)remaining.TotalDays}d {remaining:hh\\:mm\\:ss}"
                : remaining.ToString(@"hh\:mm\:ss");
        }
        else
        {
            PowerScheduleCountdownText.Text = "--:--:--";
        }

        string action = _appSettings.PowerScheduleAction.ToLowerInvariant();
        string force = _appSettings.ForcePowerAction ? " - force close apps" : string.Empty;
        DateTime clock = DateTime.Today
            .AddHours(_appSettings.PowerScheduleHour)
            .AddMinutes(_appSettings.PowerScheduleMinute);
        string clock12 = clock.ToString("h:mm tt", CultureInfo.InvariantCulture);
        PowerScheduleStatusText.Text = next.HasValue
            ? $"Daily {action} at {_appSettings.PowerScheduleHour:00}:{_appSettings.PowerScheduleMinute:00} ({clock12}){force}. Next: {next.Value:dd/MM/yyyy HH:mm}."
            : $"Daily {action} at {_appSettings.PowerScheduleHour:00}:{_appSettings.PowerScheduleMinute:00} ({clock12}){force}.";
    }

    private DateTime? CalculateNextPowerActionForDisplay()
    {
        if (!_appSettings.PowerScheduleEnabled)
        {
            return null;
        }

        DateTime now = DateTime.Now;
        DateTime scheduled = now.Date
            .AddHours(_appSettings.PowerScheduleHour)
            .AddMinutes(_appSettings.PowerScheduleMinute);
        return scheduled > now ? scheduled : scheduled.AddDays(1);
    }


    private static double NormalizeCpuAlertThreshold(double value) =>
        Math.Clamp(Math.Round(value), 70.0, 105.0);

    private void SelectCpuAlertIntervalItem(int seconds)
    {
        if (CpuTemperatureAlertIntervalComboBox is null)
        {
            return;
        }

        foreach (object item in CpuTemperatureAlertIntervalComboBox.Items)
        {
            if (item is ComboBoxItem comboBoxItem &&
                int.TryParse(comboBoxItem.Tag?.ToString(), out int itemSeconds) &&
                itemSeconds == seconds)
            {
                CpuTemperatureAlertIntervalComboBox.SelectedItem = comboBoxItem;
                return;
            }
        }

        int normalized = NormalizeCpuAlertInterval(seconds);
        _appSettings.CpuTemperatureAlertIntervalSeconds = normalized;
        SelectCpuAlertIntervalItem(normalized);
    }

    private static int NormalizeCpuAlertInterval(int seconds)
    {
        int[] allowed = { 60, 120, 300, 600 };
        return allowed.Contains(seconds)
            ? seconds
            : allowed.OrderBy(value => Math.Abs(value - seconds)).First();
    }

    private static string FormatCpuAlertInterval(int seconds)
    {
        int minutes = Math.Max(1, seconds / 60);
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    private static int NormalizePingInterval(int seconds)
    {
        int[] allowed = { 10, 15, 30, 60, 120, 300, 600, 1800 };
        return allowed.Contains(seconds)
            ? seconds
            : allowed.OrderBy(value => Math.Abs(value - seconds)).First();
    }

    private static string FormatPingInterval(int seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds} seconds";
        }

        int minutes = seconds / 60;
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _loadingSettings || StartupCheckBox is null || StatusText is null)
        {
            return;
        }

        try
        {
            _startupService.SetEnabled(StartupCheckBox.IsChecked == true);
            StatusText.Text = StartupCheckBox.IsChecked == true
                ? "Elevated dashboard startup enabled 20 seconds after sign-in. It will run with highest privileges; the background service starts independently at boot."
                : "Elevated dashboard startup disabled. Background monitoring remains controlled by the LocalSystem Windows service.";
        }
        catch (Exception exception)
        {
            _loadingSettings = true;
            StartupCheckBox.IsChecked = _startupService.IsEnabled();
            _loadingSettings = false;
            WpfMessageBox.Show(exception.Message, "Windows startup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private DeviceEntry? RequireSelectedDevice()
    {
        DeviceEntry? selected = SelectedDevice;
        if (selected is null)
        {
            WpfMessageBox.Show(
                "Select a device first.",
                "Device selection",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        return selected;
    }

    private async Task SaveDevicesAsync()
    {
        try
        {
            await _storageService.SaveAsync(Devices);
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(
                $"The device list could not be saved.\n\n{exception.Message}",
                "Save devices",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void UpdateDeviceCount() => DeviceCountText.Text = $"{Devices.Count} device(s)";

    /// <summary>
    /// Splits detected fans across the CPU card (CPU plus case fans) and the GPU card.
    /// </summary>
    private void ApplyFanInventory(FanInventoryReading inventory)
    {
        if (!inventory.IsAvailable)
        {
            CaseFanSpeedText.Text = "Case fans: not detected";
            CaseFanSpeedText.ToolTip = inventory.Diagnostic;
            GpuFanSpeedText.Text = "GPU fans: not detected";
            GpuFanSpeedText.ToolTip = inventory.Diagnostic;
            return;
        }

        List<FanSensorReading> caseFans = inventory.CpuAndSystemFans
            .Where(fan => fan.Group == FanGroup.System)
            .ToList();
        List<FanSensorReading> gpuFans = inventory.GpuFans.ToList();

        CaseFanSpeedText.Text = caseFans.Count > 0
            ? "Case fans: " + string.Join(", ", caseFans.Select(fan => $"{fan.Rpm:0} RPM"))
            : "Case fans: none reporting";
        CaseFanSpeedText.ToolTip = caseFans.Count > 0
            ? string.Join(Environment.NewLine, caseFans.Select(fan => $"{fan.Name}: {fan.Rpm:0} RPM"))
            : inventory.Diagnostic;

        // Many cards stop their fans entirely when idle, so 0 RPM here is normal, not a fault.
        GpuFanSpeedText.Text = gpuFans.Count > 0
            ? "GPU fans: " + string.Join(", ", gpuFans.Select(fan => $"{fan.Rpm:0} RPM"))
            : "GPU fans: not exposed by the driver";
        GpuFanSpeedText.ToolTip = gpuFans.Count > 0
            ? string.Join(Environment.NewLine, gpuFans.Select(fan => $"{fan.Name}: {fan.Rpm:0} RPM"))
            : inventory.Diagnostic;
    }

    /// <summary>
    /// Reads all fans and the GPU sensors in this process. Runs whether or not the background
    /// service is supplying the CPU temperature.
    /// </summary>
    private async Task RefreshFanAndGpuSensorsAsync()
    {
        try
        {
            FanInventoryReading fanInventory = await Task.Run(
                _hardwareMonitorService.GetFanInventoryReading);
            ApplyFanInventory(fanInventory);
            _lastFanInventory = fanInventory;
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Refresh fan inventory", exception);
            CaseFanSpeedText.Text = "Case fans: error";
            GpuFanSpeedText.Text = "GPU fans: error";
        }

        await RefreshGpuSensorsAsync();

        // Interpret what has just been read. Nothing is sampled twice.
        try
        {
            RefreshPcHealth(
                await Task.Run(_hardwareMonitorService.GetCpuTemperatureReading),
                await Task.Run(_hardwareMonitorService.GetGpuTemperatureReading),
                _lastFanInventory,
                _lastMemoryUsagePercent,
                _lastNetworkConnected,
                _lastNetworkSummary);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Refresh PC health strip", exception);
        }
    }

    private async Task RefreshGpuSensorsAsync()
    {
        try
        {
            GpuTemperatureReading temperature = await Task.Run(_hardwareMonitorService.GetGpuTemperatureReading);
            GpuUtilisationReading utilisation = await Task.Run(_hardwareMonitorService.GetGpuUtilisationReading);
            GpuMemoryReading memory = await Task.Run(_hardwareMonitorService.GetGpuMemoryReading);

            GpuTemperatureText.Text = temperature.IsAvailable
                ? $"Temperature: {temperature.Display}"
                : "Temperature: not available";
            GpuTemperatureText.ToolTip = temperature.IsAvailable
                ? $"{temperature.SensorName} - {temperature.Source}"
                : temperature.Diagnostic;

            GpuUtilisationText.Text = utilisation.IsAvailable
                ? $"Utilisation: {utilisation.Display}"
                : "Utilisation: not available";
            GpuUtilisationText.ToolTip = utilisation.IsAvailable
                ? $"{utilisation.SensorName} - {utilisation.Source}"
                : utilisation.Diagnostic;

            GpuMemoryText.Text = memory.IsAvailable
                ? $"VRAM: {memory.Display}"
                : "VRAM: not available";
            GpuMemoryText.ToolTip = memory.IsAvailable
                ? $"{memory.SensorName} - {memory.Source}"
                : memory.Diagnostic;
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Refresh GPU sensors", exception);
            GpuTemperatureText.Text = "Temperature: error";
            GpuUtilisationText.Text = "Utilisation: error";
            GpuMemoryText.Text = "VRAM: error";
        }
    }


    /// <summary>
    /// View-model row for the health strip. Carries its own brush so the template can bind a
    /// colour without a converter.
    /// </summary>
    private sealed record HealthDomainRow(string Name, string Headline, string Reason, WpfBrush StatusBrush);

    private static WpfBrush BrushForHealth(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A)),
        HealthStatus.Attention => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06)),
        HealthStatus.ActionNeeded => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26)),
        _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF))
    };

    /// <summary>
    /// Builds the verdict strip. Reads nothing new from hardware - it interprets readings the
    /// dashboard has already taken, so it adds no extra sensor cost.
    /// </summary>
    private void RefreshPcHealth(
        CpuTemperatureReading cpuTemperature,
        GpuTemperatureReading gpuTemperature,
        FanInventoryReading fans,
        double memoryUsagePercent,
        bool networkConnected,
        string networkSummary)
    {
        try
        {
            List<HealthDomain> domains = new()
            {
                _pcHealthService.AssessCooling(cpuTemperature, gpuTemperature, fans),
                _pcHealthService.AssessStorage(_pcHealthStorageService.GetTestableVolumes()),
                _pcHealthService.AssessMemory(memoryUsagePercent),
                _pcHealthService.AssessNetwork(networkConnected, networkSummary)
            };

            HealthStatus overall = PcHealthService.Overall(domains);
            HealthOverallText.Text = PcHealthService.DescribeOverall(overall);
            HealthStatusDot.Background = BrushForHealth(overall);

            int flagged = domains.Count(domain => domain.Status is HealthStatus.Attention or HealthStatus.ActionNeeded);
            HealthSummaryText.Text = flagged == 0
                ? "Cooling, storage, memory and network all look normal."
                : $"{flagged} of {domains.Count} checks need attention.";

            HealthDomainList.ItemsSource = domains
                .Select(domain => new HealthDomainRow(
                    domain.Name, domain.Headline, domain.Reason, BrushForHealth(domain.Status)))
                .ToList();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Refresh PC health", exception);
        }
    }


    private void CpuCard_Click(object sender, MouseButtonEventArgs e) => OpenComponentGraph(GraphComponent.Cpu);

    private void GpuCard_Click(object sender, MouseButtonEventArgs e) => OpenComponentGraph(GraphComponent.Gpu);

    private void MemoryCard_Click(object sender, MouseButtonEventArgs e) => OpenComponentGraph(GraphComponent.Memory);

    private void OpenComponentGraph(GraphComponent component)
    {
        try
        {
            ComponentGraphWindow window = new(component) { Owner = this };
            window.Show();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Open component graph", exception);
            WpfMessageBox.Show(exception.Message, "Live readings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private void TrayIconToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _appSettings.TrayIconEnabled = TrayIconToggle.IsChecked == true;
        ApplyTrayIconVisibility();
        _ = PersistSettingsAsync();
    }

    private void DesktopNotificationsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _appSettings.DesktopNotificationsEnabled = DesktopNotificationsToggle.IsChecked == true;
        _ = PersistSettingsAsync();
    }

    /// <summary>
    /// Hiding the icon does not close the dashboard; it keeps running and can be reopened from
    /// Start. Warn once, because a hidden icon plus a window close looks like the app vanished.
    /// </summary>
    private void ApplyTrayIconVisibility()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Visible = _appSettings.TrayIconEnabled;
        TrayIconHintText.Text = _appSettings.TrayIconEnabled
            ? "The icon shows background service status and reopens the dashboard."
            : "Icon hidden. Closing this window will leave no visible trace of the dashboard - reopen it from Start.";
    }

    private async Task PersistSettingsAsync()
    {
        try
        {
            await _settingsService.SaveAsync(_appSettings);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save notification settings", exception);
        }
    }

}
