using System.Windows;
using System.Windows.Controls;
using WindowsUtility.Models;
using WindowsUtility.Services;

namespace WindowsUtility.Dialogs;

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

    /// <summary>Most recent successful or failed check, so the dashboard can refresh its update alert.</summary>
    public UpdateCheckResult? LastResult { get; private set; }

    public UpdateWindow(AppSettings settings, AppSettingsService settingsService)
    {
        InitializeComponent();
        _settings = settings;
        _settingsService = settingsService;
        DeveloperText.Text = UpdateService.DeveloperName;
        ShowInstalledVersion(UpdateService.GetCurrentReleaseDate());
        AutoCheckBox.IsChecked = settings.AutoCheckForUpdates;
        AutoInstallCheckBox.IsChecked = settings.AutoInstallUpdates;
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
        SetDownloadSize(null);
        SetNotes(null, null);
        LastCheckedText.Text = "Checking now...";
        ShowStatus(CheckingIcon, "PrimaryBrush", "Checking for updates...", "Contacting the release server.");

        try
        {
            UpdateCheckResult result = await UpdateService.CheckAsync(_lifetime.Token);
            LastResult = result;
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
            $"Windows Utility {_currentVersion} is the latest version. There is nothing to install.");

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
        _ = ShowDownloadSizeAsync(manifest);
    }

    private async Task ShowDownloadSizeAsync(UpdateManifest manifest)
    {
        SetDownloadSize("Checking...");
        long? size = await UpdateService.GetDownloadSizeAsync(manifest, _lifetime.Token);
        if (ReferenceEquals(manifest, _available))
        {
            SetDownloadSize(size is > 0 ? UpdateService.FormatBytes(size.Value) : "Not reported by the server");
        }
    }

    private void SetDownloadSize(string? text)
    {
        Visibility visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        DownloadSizeLabel.Visibility = visibility;
        DownloadSizeText.Visibility = visibility;
        DownloadSizeText.Text = text ?? string.Empty;
    }

    /// <summary>Turns the CHANGELOG markdown in update.json into readable plain text.</summary>
    private static string FormatReleaseNotes(string notes)
    {
        List<string> lines = new();
        foreach (string raw in notes.Replace("\r", string.Empty).Split('\n'))
        {
            string line = raw.Replace("**", string.Empty).Replace("`", string.Empty);
            string trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                if (lines.Count > 0)
                {
                    lines.Add(string.Empty);
                }

                lines.Add(trimmed.TrimStart('#').Trim());
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                bool nested = line.StartsWith("  ", StringComparison.Ordinal);
                lines.Add((nested ? "      –  " : "•  ") + trimmed[2..]);
            }
            else if (trimmed.Length > 0 && lines.Count > 0 && lines[^1].Length > 0 && line.StartsWith("  ", StringComparison.Ordinal))
            {
                lines[^1] += " " + trimmed; // wrapped continuation of the previous bullet
            }
            else
            {
                lines.Add(trimmed);
            }
        }

        return string.Join(Environment.NewLine, lines).Trim();
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
            Progress<Services.DownloadProgress> progress = new(value =>
            {
                DownloadProgress.Value = value.Fraction;
                StatusDetail.Text = value.Describe();
                if (value.Total is > 0)
                {
                    SetDownloadSize(UpdateService.FormatBytes(value.Total.Value));
                }
            });

            SelfUpdateOutcome outcome = await SelfUpdateInstaller.InstallAsync(_available, progress, automatic: false, _lifetime.Token);
            if (outcome.Route == SelfUpdateRoute.Installer)
            {
                ShowStatus(UpToDateIcon, "SuccessBrush", "Download verified. Installing...", outcome.Message);
                System.Windows.Application.Current.Shutdown();
                return;
            }

            // The background service installs it; the installer closes this app and the service reopens it.
            DownloadProgress.Visibility = Visibility.Collapsed;
            ShowStatus(UpToDateIcon, "SuccessBrush", outcome.Route == SelfUpdateRoute.UpToDate ? "Already up to date" : "Installing...", outcome.Message);
            if (outcome.Route == SelfUpdateRoute.UpToDate)
            {
                SetBusy(false);
            }
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
        _settings.AutoInstallUpdates = AutoInstallCheckBox.IsChecked == true;
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
            ? $"Windows Utility {_currentVersion}  •  Developed by {UpdateService.DeveloperName}"
            : $"Windows Utility {_currentVersion}  •  Published {date}  •  Developed by {UpdateService.DeveloperName}";
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
        NotesText.Text = show ? FormatReleaseNotes(notes!) : string.Empty;
        NotesScroller.ScrollToTop();
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
