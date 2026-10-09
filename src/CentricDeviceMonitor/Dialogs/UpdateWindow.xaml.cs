using System.Windows;
using System.Windows.Controls;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.Dialogs;

public partial class UpdateWindow : Window
{
    private const string CheckingIcon = "";
    private const string UpToDateIcon = "";
    private const string AvailableIcon = "";
    private const string FailedIcon = "";

    private readonly AppSettings _settings;
    private readonly AppSettingsService _settingsService;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Version _currentVersion = UpdateService.GetCurrentVersion();
    private UpdateManifest? _available;
    private Version? _availableVersion;
    private bool _busy;
    private bool _loadingSettings = true;

    public UpdateWindow(AppSettings settings, AppSettingsService settingsService)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;
        ShowInstalledVersion(UpdateService.GetCurrentReleaseDate());
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
        _availableVersion = null;
        UpdateButton.Visibility = Visibility.Collapsed;
        SetLatest(null);
        SetNotes(null, null);
        LastCheckedText.Text = "Checking now...";
        ShowStatus(CheckingIcon, "PrimaryBrush", "Checking for updates...", "Contacting the release server.");

        try
        {
            UpdateCheckResult result = await UpdateService.CheckAsync(_lifetime.Token);
            LastCheckedText.Text = $"Today at {DateTime.Now:t}";

            switch (result.Status)
            {
                case UpdateCheckStatus.UpToDate:
                    ShowUpToDate(result);
                    break;
                case UpdateCheckStatus.Available when result.Manifest is not null && result.LatestVersion is not null:
                    ShowAvailable(result.Manifest, result.LatestVersion);
                    break;
                default:
                    ShowStatus(FailedIcon, "WarningBrush", "Couldn't check for updates", result.Message);
                    LastCheckedText.Text = $"Today at {DateTime.Now:t} (failed)";
                    break;
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

    private void ShowUpToDate(UpdateCheckResult result)
    {
        ShowStatus(UpToDateIcon, "SuccessBrush", "You're up to date",
            $"Windows Utility by Sajith {_currentVersion} is the latest version. There is nothing to install.");

        if (result.Manifest is not null && result.LatestVersion == _currentVersion)
        {
            // The published release is this build, so its release date is the authoritative one.
            DateTime? released = UpdateService.ParseReleaseDate(result.Manifest.Released);
            if (released is not null)
            {
                ShowInstalledVersion(released);
            }

            SetNotes("What's in this version", result.Manifest.Notes);
        }
        else if (result.LatestVersion is not null && result.LatestVersion < _currentVersion)
        {
            SetLatest($"{result.LatestVersion}{ReleasedSuffix(result.Manifest)}  (this build is newer)");
        }
    }

    private void ShowAvailable(UpdateManifest manifest, Version latest)
    {
        _available = manifest;
        _availableVersion = latest;

        DateTime? released = UpdateService.ParseReleaseDate(manifest.Released);
        string detail = released is null
            ? $"A newer version is ready to install. You have {_currentVersion}."
            : $"Published {UpdateService.FormatReleaseDate(released.Value)}. You have {_currentVersion}.";

        ShowStatus(AvailableIcon, "PrimaryBrush", $"Version {latest} is available", detail);
        SetLatest($"{latest}{ReleasedSuffix(manifest)}");
        SetNotes($"What's new in {latest}",
            string.IsNullOrWhiteSpace(manifest.Notes) ? "No release notes were provided." : manifest.Notes);
        UpdateButton.Content = $"Update to {latest}";
        UpdateButton.Visibility = Visibility.Visible;
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
        ShowStatus(AvailableIcon, "PrimaryBrush", $"Downloading version {_availableVersion}...", "Starting the download.");

        try
        {
            Progress<double> progress = new(value =>
            {
                DownloadProgress.Value = value;
                StatusDetail.Text = $"{value:P0} downloaded. The installer is checked against its published SHA-256 before it runs.";
            });

            string installerPath = await UpdateService.DownloadAsync(_available, progress, _lifetime.Token);
            ShowStatus(UpToDateIcon, "SuccessBrush", "Download verified. Installing...",
                "The app will close now and reopen when the update has finished.");

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
            ShowStatus(FailedIcon, "DangerBrush", "The update could not be installed", exception.Message);
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

    private void ShowInstalledVersion(DateTime? published)
    {
        string date = published is null ? "Unknown" : UpdateService.FormatReleaseDate(published.Value);
        InstalledVersionText.Text = _currentVersion.ToString();
        InstalledDateText.Text = date;
        CurrentVersionText.Text = published is null
            ? $"Windows Utility by Sajith {_currentVersion}"
            : $"Windows Utility by Sajith {_currentVersion}  •  Published {date}";
    }

    private void ShowStatus(string icon, string brushKey, string title, string detail)
    {
        StatusIcon.Text = icon;
        StatusBadge.SetResourceReference(Border.BackgroundProperty, brushKey);
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
    }

    private void SetLatest(string? text)
    {
        Visibility visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        LatestLabel.Visibility = visibility;
        LatestVersionText.Visibility = visibility;
        LatestVersionText.Text = text ?? string.Empty;
    }

    private void SetNotes(string? header, string? notes)
    {
        bool show = !string.IsNullOrWhiteSpace(notes);
        NotesPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        NotesHeader.Text = header ?? string.Empty;
        NotesText.Text = show ? notes!.Trim() : string.Empty;
    }

    private static string ReleasedSuffix(UpdateManifest? manifest)
    {
        DateTime? released = UpdateService.ParseReleaseDate(manifest?.Released);
        return released is null ? string.Empty : $"  •  {UpdateService.FormatReleaseDate(released.Value)}";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        UpdateButton.IsEnabled = !busy;
    }
}
