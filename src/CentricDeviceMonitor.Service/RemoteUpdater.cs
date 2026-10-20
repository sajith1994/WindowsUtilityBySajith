using System.Diagnostics;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.ServiceHost;

/// <summary>
/// Updates this PC when the primary PC asks. The request only says "update": this PC checks the official release
/// itself, downloads the installer from GitHub and verifies its SHA-256 (the same checks as the dashboard's own
/// updater), then runs it silently. The installer stops and replaces this service, and reopens the dashboard for
/// the signed-in user if it was open.
/// </summary>
internal sealed class RemoteUpdater
{
    private const string DashboardProcessName = "WindowsUtilityBySajith";
    private int _running;

    /// <summary>Shown on the primary PC's tile while an update runs, or why the last one failed.</summary>
    public string Status { get; private set; } = string.Empty;

    public async Task<RemoteUpdateResult> StartAsync(string requestedBy)
    {
        string current = UpdateService.GetCurrentVersion().ToString(3);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return new RemoteUpdateResult { Status = "Busy", CurrentVersion = current, Message = Status };
        }

        UpdateCheckResult check;
        try
        {
            check = await UpdateService.CheckAsync();
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _running, 0);
            return Failed(current, $"Could not check for updates: {exception.Message}");
        }

        string latest = check.LatestVersion?.ToString(3) ?? string.Empty;
        if (check.Status == UpdateCheckStatus.UpToDate)
        {
            Interlocked.Exchange(ref _running, 0);
            Status = string.Empty;
            return new RemoteUpdateResult { Status = "UpToDate", CurrentVersion = current, LatestVersion = latest, Message = check.Message };
        }

        if (check.Status != UpdateCheckStatus.Available || check.Manifest is null)
        {
            Interlocked.Exchange(ref _running, 0);
            return Failed(current, check.Message);
        }

        Status = $"Downloading {latest}...";
        ServiceLog.Write("Remote update", $"{requestedBy} asked for an update from {current} to {latest}.");
        _ = Task.Run(() => DownloadAndInstallAsync(check.Manifest, latest));
        return new RemoteUpdateResult { Status = "Started", CurrentVersion = current, LatestVersion = latest, Message = $"Updating from {current} to {latest}." };
    }

    private async Task DownloadAndInstallAsync(UpdateManifest manifest, string latest)
    {
        try
        {
            string installer = await UpdateService.DownloadAsync(manifest, null, CancellationToken.None);
            Status = $"Installing {latest}...";

            // Reopen the dashboard afterwards only if someone had it open.
            string marker = Path.Combine(SharedDataPaths.RootDirectory, "remote-update-relaunch.flag");
            if (Process.GetProcessesByName(DashboardProcessName).Length > 0)
            {
                File.WriteAllText(marker, DateTime.Now.ToString("O"));
            }
            else if (File.Exists(marker))
            {
                File.Delete(marker);
            }

            ServiceLog.Write("Remote update", $"Starting the verified installer for {latest}; this service restarts when it finishes.");
            Process.Start(new ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /REMOTEUPDATE")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(installer) ?? string.Empty
            });
        }
        catch (Exception exception)
        {
            Status = $"Update failed: {exception.Message}";
            ServiceLog.WriteException("Remote update", exception);
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private RemoteUpdateResult Failed(string current, string message)
    {
        Status = $"Update failed: {message}";
        ServiceLog.Write("Remote update", Status);
        return new RemoteUpdateResult { Status = "Failed", CurrentVersion = current, Message = message };
    }
}
