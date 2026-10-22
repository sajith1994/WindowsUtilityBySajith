using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CentricDeviceMonitor.Services;
using WpfApplication = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor;

public partial class App : WpfApplication
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private SafeWpfInputLanguageSource? _safeInputLanguageSource;

    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        int toolIndex = Array.FindIndex(e.Args, argument => string.Equals(argument, ElevatedTool.Argument, StringComparison.OrdinalIgnoreCase));
        if (toolIndex >= 0 && toolIndex + 1 < e.Args.Length)
        {
            // A second, elevated copy opened for one administrator-only tool; the dashboard keeps running.
            await RunToolAsync(e.Args[toolIndex + 1], toolIndex + 2 < e.Args.Length ? e.Args[toolIndex + 2] : null);
            return;
        }

        bool startedByWindows = e.Args.Any(argument =>
            string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
        bool replacingExistingInstance = e.Args.Any(argument =>
            string.Equals(argument, "--elevated-restart", StringComparison.OrdinalIgnoreCase));

        // Runs as the signed-in user. Administrator-only work goes through the background service (allowed for
        // standard users by the administrator's policy) or asks for elevation only for the tool that needs it.
        ApplicationLogService.WriteMessage("Startup",
            new WindowsUtilityService().IsRunningAsAdministrator() ? "Dashboard started with administrator rights." : "Dashboard started as a standard user.");

        if (startedByWindows)
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
        }

        _singleInstanceMutex = new Mutex(
            initiallyOwned: false,
            name: @"Local\CentricDeviceMonitor.SingleInstance");

        try
        {
            _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(
                replacingExistingInstance ? TimeSpan.FromSeconds(8) : TimeSpan.Zero,
                exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            _ownsSingleInstanceMutex = true;
        }

        if (!_ownsSingleInstanceMutex)
        {
            // Another instance owns the dashboard. Ask it to show itself instead of telling the
            // user it is "already running" - with the tray icon hidden that message left them no
            // way back to a minimised window.
            if (!startedByWindows && !replacingExistingInstance)
            {
                bool signalled = TrySignalExistingInstance();
                if (!signalled)
                {
                    WpfMessageBox.Show(
                        "Windows Utility is already running but did not respond. " +
                        "Check Task Manager for WindowsUtilityBySajith.",
                        "Windows Utility",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }

            Shutdown();
            return;
        }

        StartActivationListener();

        try
        {
            ApplicationLogService.WriteMessage("Startup", $"Application starting. Version {typeof(App).Assembly.GetName().Version}.");

            // WPF's native InputLanguageSource still maps the keyboard LANGID through the
            // legacy CultureInfo(int) path. Windows 10/11 can report 0x1000 for valid
            // custom/BCP-47 locales, which makes focused TextBoxes throw CultureNotFoundException.
            // Register a culture-safe source before any application window receives focus.
            _safeInputLanguageSource = new SafeWpfInputLanguageSource();
            InputLanguageManager.Current.RegisterInputLanguageSource(_safeInputLanguageSource);

            ThemeManager.Apply(ThemeManager.SystemPreference);
            WindowFitter.Register();
            UpdateService.ClearRelaunchMarker();
            MainWindow mainWindow = new();
            MainWindow = mainWindow;
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Startup", exception);
            WpfMessageBox.Show(
                $"Windows Utility could not start.\n\n{exception.Message}\n\nDiagnostic log: {ApplicationLogService.LogFilePath}",
                "Windows Utility",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>Opens only the requested tool (no tray icon, no single-instance check), then exits with it.</summary>
    private async Task RunToolAsync(string tool, string? argument)
    {
        try
        {
            _safeInputLanguageSource = new SafeWpfInputLanguageSource();
            InputLanguageManager.Current.RegisterInputLanguageSource(_safeInputLanguageSource);
            Models.AppSettings settings = await new AppSettingsService().LoadAsync();
            ThemeManager.UseWindowsAccent = settings.UseWindowsAccentColor;
            ThemeManager.Apply(settings.UiThemePreference);
            WindowFitter.Register();
            ApplicationLogService.WriteMessage("Elevated tool", $"Opening {tool} as {Environment.UserName}.");

            if (!PrivilegedOperations.IsAdministrator)
            {
                WpfMessageBox.Show("This tool needs administrator rights.", "Windows Utility", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            if (tool == ElevatedTool.ApplyAdapterSettings && argument is not null)
            {
                await ApplyAdapterSettingsAsync(argument);
                Shutdown();
                return;
            }

            Window? window = tool switch
            {
                ElevatedTool.DiskTools => new Dialogs.DiskPreparationWindow { Title = "Disk tools (administrator)" },
                ElevatedTool.DiskClone => new Dialogs.DiskCloneWindow(),
                ElevatedTool.ThermalTest => new Dialogs.ThermalLoadTestWindow(),
                _ => null
            };
            if (window is null)
            {
                Shutdown();
                return;
            }

            MainWindow = window;
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException($"Elevated tool {tool}", exception);
            WpfMessageBox.Show($"The tool could not be opened.\n\n{exception.Message}", "Windows Utility", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>The elevated half of changing an adapter's IP settings for a standard user's dashboard.</summary>
    private static async Task ApplyAdapterSettingsAsync(string requestPath)
    {
        MaintenanceResult result;
        try
        {
            AdapterSettingsRequest request = System.Text.Json.JsonSerializer.Deserialize<AdapterSettingsRequest>(await System.IO.File.ReadAllTextAsync(requestPath))
                ?? throw new InvalidOperationException("The request was empty.");
            IReadOnlyList<string> problems = NetworkAdapterConfigService.Validate(request.UseDhcp, request.Address, request.Mask, request.Gateway,
                request.AutomaticDns ? NetworkAdapterConfigService.DnsPresets[0] : NetworkAdapterConfigService.DnsPresets[^1],
                request.PrimaryDns, request.SecondaryDns);
            result = problems.Count > 0
                ? new MaintenanceResult(false, string.Join("\n", problems))
                : new MaintenanceResult(true, await new NetworkAdapterConfigService().ApplyAsync(request));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.IO.IOException or System.Text.Json.JsonException)
        {
            result = new MaintenanceResult(false, exception.Message);
        }

        await System.IO.File.WriteAllTextAsync(ElevatedTool.ResultPath(requestPath), System.Text.Json.JsonSerializer.Serialize(result));
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ApplicationLogService.WriteException("UI thread", e.Exception);
        WpfMessageBox.Show(
            $"An unexpected application error occurred.\n\n{e.Exception.Message}\n\nThe error was recorded in:\n{ApplicationLogService.LogFilePath}",
            "Windows Utility",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ApplicationLogService.WriteException("Unhandled exception", exception);
        }
        else
        {
            ApplicationLogService.WriteMessage("Unhandled exception", e.ExceptionObject?.ToString() ?? "Unknown error");
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ApplicationLogService.WriteException("Unobserved task", e.Exception);
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();
        ApplicationLogService.WriteMessage("Shutdown", "Application exited.");
        base.OnExit(e);
    }

    private const string ActivationEventName = @"Local\CentricDeviceMonitor.Activate";
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;

    /// <summary>
    /// The owning instance waits on a named event; a second launch sets it. Cheaper and more
    /// reliable than finding the window by title, and works when the window is hidden.
    /// </summary>
    private void StartActivationListener()
    {
        try
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                (_, _) => Dispatcher.BeginInvoke(ActivateMainWindow),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Start activation listener", exception);
        }
    }

    private static bool TrySignalExistingInstance()
    {
        try
        {
            using EventWaitHandle handle = EventWaitHandle.OpenExisting(ActivationEventName);
            return handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Signal existing instance", exception);
            return false;
        }
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        window.BringToFront();
    }
}
