using System.Diagnostics;
using System.Reflection;
using System.Windows;
using WindowsUtility.Models;
using WindowsUtility.Services;

namespace WindowsUtility.Dialogs;

public partial class AboutWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AppSettingsService _settingsService;

    public AboutWindow(AppSettings settings, AppSettingsService settingsService)
    {
        _settings = settings;
        _settingsService = settingsService;
        InitializeComponent();
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        DateTime? published = UpdateService.GetCurrentReleaseDate();
        string versionNumber = version is null ? "unavailable" : $"{version.Major}.{version.Minor}.{version.Build}";
        string publishedText = published is null ? string.Empty : $"  •  Published {UpdateService.FormatReleaseDate(published.Value)}";
        VersionText.Text = $"Version {versionNumber}{publishedText}  •  Developed by {UpdateService.DeveloperName}";
        VersionValueText.Text = published is null ? versionNumber : $"{versionNumber} (published {UpdateService.FormatReleaseDate(published.Value)})";
    }

    private void TermsButton_Click(object sender, RoutedEventArgs e)
    {
        TermsWindow termsWindow = new() { Owner = this };
        termsWindow.ShowDialog();
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateWindow updateWindow = new(_settings, _settingsService) { Owner = this };
        updateWindow.ShowDialog();
    }

    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Saves the latest verified installer to Downloads and shows it in File Explorer.</summary>
    private async void DownloadInstallerButton_Click(object sender, RoutedEventArgs e)
    {
        DownloadInstallerButton.IsEnabled = false;
        DownloadInstallerProgress.Value = 0;
        DownloadInstallerProgress.Visibility = Visibility.Visible;
        ShareStatusText.Text = "Finding the latest release...";
        Progress<DownloadProgress> progress = new(report =>
        {
            if (report.Total is long total && total > 0)
            {
                DownloadInstallerProgress.Value = (double)report.Received / total;
                ShareStatusText.Text = $"Downloading {UpdateService.FormatBytes(report.Received)} of {UpdateService.FormatBytes(total)}  •  {UpdateService.FormatBytes(report.BytesPerSecond)}/s";
            }
        });

        try
        {
            string saved = await UpdateService.SaveLatestInstallerAsync(GetDownloadsFolder(), progress, _lifetime.Token);
            ShareStatusText.Text = $"Saved and verified: {saved}";
            ApplicationLogService.WriteMessage("Share", $"Installer saved to {saved}.");
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{saved}\"") { UseShellExecute = true });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Window closed during the download.
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Download installer", exception);
            ShareStatusText.Text = $"Could not download the installer: {exception.Message}";
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                DownloadInstallerButton.IsEnabled = true;
                DownloadInstallerProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OpenReleasePageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateService.ReleasesPageUrl) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShareStatusText.Text = $"Could not open the browser: {exception.Message}";
        }
    }

    private void CopyReleaseLinkButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(UpdateService.ReleasesPageUrl);
            ShareStatusText.Text = $"Link copied: {UpdateService.ReleasesPageUrl}";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            ShareStatusText.Text = "The clipboard is busy. Try Copy link again.";
        }
    }

    /// <summary>The user's Downloads folder (it may have been moved), or the profile's Downloads as a fallback.</summary>
    private static string GetDownloadsFolder()
    {
        Guid downloads = new("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref downloads, 0, IntPtr.Zero, out IntPtr path) == 0)
        {
            try
            {
                string? folder = System.Runtime.InteropServices.Marshal.PtrToStringUni(path);
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    return folder;
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(path);
            }
        }

        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        base.OnClosed(e);
    }
}
