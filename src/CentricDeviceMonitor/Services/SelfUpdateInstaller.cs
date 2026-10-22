using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public enum SelfUpdateRoute
{
    /// <summary>The background service downloads, verifies and installs it; the dashboard is closed and reopened for the user.</summary>
    Service,

    /// <summary>The installer was started from the dashboard (Windows asks for an administrator first if needed).</summary>
    Installer,

    /// <summary>Nothing started: no background service and no administrator rights, so an automatic install would prompt.</summary>
    NeedsAdministrator,

    UpToDate
}

public sealed record SelfUpdateOutcome(SelfUpdateRoute Route, string Message);

/// <summary>
/// Installs an update of this PC's Windows Utility. The background service does it when it is running (it downloads
/// the official release and checks it itself, then runs the installer as SYSTEM), so a standard user's dashboard needs
/// no administrator password. Without the service the installer is started directly as before.
/// </summary>
public static class SelfUpdateInstaller
{
    /// <param name="automatic">An unattended install: never starts something that would ask for an administrator password.</param>
    public static async Task<SelfUpdateOutcome> InstallAsync(UpdateManifest manifest, IProgress<DownloadProgress>? progress, bool automatic,
        CancellationToken cancellationToken)
    {
        if (await IsServiceRunningAsync())
        {
            try
            {
                RemoteUpdateResult result = await PrivilegedOperations.RunAsync<RemoteUpdateResult>(PrivilegedServiceActions.InstallUpdate, null,
                    TimeSpan.FromMinutes(1), cancellationToken);
                switch (result.Status)
                {
                    case "Started" or "Busy":
                        return new SelfUpdateOutcome(SelfUpdateRoute.Service,
                            $"The background service is downloading and installing {(result.LatestVersion.Length > 0 ? result.LatestVersion : "the update")}. Windows Utility closes for about a minute and opens again by itself.");
                    case "UpToDate":
                        return new SelfUpdateOutcome(SelfUpdateRoute.UpToDate, result.Message);
                    default:
                        throw new InvalidOperationException(result.Message);
                }
            }
            catch (Exception exception) when (exception is TimeoutException
                || exception is InvalidOperationException && exception.Message.Contains("not supported", StringComparison.OrdinalIgnoreCase))
            {
                // The service did not answer, or it is an older version without this action: install directly below.
                ApplicationLogService.WriteMessage("Update", $"The background service could not install the update ({exception.Message}); starting the installer instead.");
            }
        }

        if (automatic && !PrivilegedOperations.IsAdministrator)
        {
            return new SelfUpdateOutcome(SelfUpdateRoute.NeedsAdministrator,
                "The background service is not running, so installing needs an administrator. Use Update now (Windows asks for an administrator password), or start the service on System Stats.");
        }

        string installerPath = await UpdateService.DownloadAsync(manifest, progress, cancellationToken);
        UpdateService.LaunchInstaller(installerPath);
        return new SelfUpdateOutcome(SelfUpdateRoute.Installer, "The app will close now and reopen when the update has finished.");
    }

    /// <summary>The service wrote its status in the last half minute.</summary>
    private static async Task<bool> IsServiceRunningAsync()
    {
        BackgroundServiceSnapshot? snapshot = await new BackgroundServiceRuntimeStore().LoadAsync();
        return snapshot is not null && DateTime.Now - snapshot.UpdatedAt < TimeSpan.FromSeconds(30);
    }
}
