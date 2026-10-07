using System.Windows;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.Dialogs;

public partial class UpdateWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AppSettingsService _settingsService;
    private readonly CancellationTokenSource _lifetime = new();
    private UpdateManifest? _available;
    private bool _busy;
    private bool _loadingSettings = true;

    public UpdateWindow(AppSettings settings, AppSettingsService settingsService)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;
        CurrentVersionText.Text = $"Installed version {UpdateService.GetCurrentVersion()}";
        AutoCheckBox.IsChecked = settings.AutoCheckForUpdates;
        _loadingSettings = false;
        Loaded += async (_, _) => await RunCheckAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }

    private async Task RunCheckAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        _available = null;
        UpdateButton.Visibility = Visibility.Collapsed;
        NotesText.Text = string.Empty;
        StatusText.Text = "Checking for updates...";

        try
        {
            UpdateCheckResult result = await UpdateService.CheckAsync(_lifetime.Token);
            StatusText.Text = result.Message;

            if (result.Status == UpdateCheckStatus.Available && result.Manifest is not null)
            {
                _available = result.Manifest;
                string released = string.IsNullOrWhiteSpace(result.Manifest.Released) ? string.Empty : $"Released {result.Manifest.Released}\n\n";
                NotesText.Text = released + (string.IsNullOrWhiteSpace(result.Manifest.Notes) ? "No release notes were provided." : result.Manifest.Notes);
                UpdateButton.Content = $"Update to {result.LatestVersion}";
                UpdateButton.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed while checking.
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_available is null || _busy)
        {
            return;
        }

        SetBusy(true);
        DownloadProgress.Value = 0;
        DownloadProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Downloading the update...";

        try
        {
            Progress<double> progress = new(value =>
            {
                DownloadProgress.Value = value;
                StatusText.Text = $"Downloading the update... {value:P0}";
            });

            string installerPath = await UpdateService.DownloadAsync(_available, progress, _lifetime.Token);
            StatusText.Text = "Verified. Installing...";

            UpdateService.LaunchInstaller(installerPath);
            System.Windows.Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            // Window closed during the download.
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Update install", exception);
            StatusText.Text = $"The update could not be installed: {exception.Message}";
            DownloadProgress.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e) => await RunCheckAsync();

    private async void AutoCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        _settings.AutoCheckForUpdates = AutoCheckBox.IsChecked == true;
        try
        {
            await _settingsService.SaveAsync(_settings);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save update preference", exception);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        UpdateButton.IsEnabled = !busy;
    }
}
