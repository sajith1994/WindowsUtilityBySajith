using System.IO;

namespace WindowsUtility.Services;

public static class SharedDataPaths
{
    /// <summary>Name of the data folders, in ProgramData (shared) and in each user's AppData\Local.</summary>
    public const string FolderName = "WindowsUtility";

    /// <summary>The data folders' name before 2.0.85.</summary>
    private const string OldFolderName = "CentricDeviceMonitor";

    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        FolderName);

    /// <summary>This user's own folder (the application log).</summary>
    public static string UserDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        FolderName);

    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public static string ServiceDirectory => Path.Combine(RootDirectory, "service");

    public static string DevicesFilePath => Path.Combine(RootDirectory, "devices.json");

    public static string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    public static string CpuTemperatureLogFilePath => Path.Combine(LogsDirectory, "cpu-temperature-events.json");

    public static string CpuTemperatureSampleLogFilePath => Path.Combine(LogsDirectory, "cpu-temperature-samples.csv");

    public static string SystemHealthLogFilePath => Path.Combine(LogsDirectory, "system-health-events.json");

    public static string FanDiagnosticsFilePath => Path.Combine(LogsDirectory, "fan-diagnostics.txt");

    public static string ServiceSnapshotFilePath => Path.Combine(ServiceDirectory, "status.json");

    public static string ServiceLogFilePath => Path.Combine(ServiceDirectory, "service.log");

    public static string RemoteViewersFilePath => Path.Combine(ServiceDirectory, "remote-viewers.json");

    /// <summary>Monitored PCs as the dashboard last saw them, served to phones by the service (written by the dashboard).</summary>
    public static string PhoneViewDataFilePath => Path.Combine(RootDirectory, "phone-view.json");

    public static string DashboardTrayReadyFilePath => Path.Combine(ServiceDirectory, "dashboard-tray.ready");

    /// <summary>Automatic backups (written by the dashboard) and how their last runs went (written by whoever ran them).</summary>
    public static string BackupSchedulesFilePath => Path.Combine(RootDirectory, "backup-schedules.json");

    public static string BackupScheduleStatesFilePath => Path.Combine(ServiceDirectory, "backup-schedule-states.json");

    public static string PrivilegedCommandsDirectory => Path.Combine(ServiceDirectory, "commands");

    public static string PrivilegedCommandRequestsDirectory => Path.Combine(PrivilegedCommandsDirectory, "requests");

    public static string PrivilegedCommandResponsesDirectory => Path.Combine(PrivilegedCommandsDirectory, "responses");

    public static string GetPrivilegedCommandRequestPath(Guid id) =>
        Path.Combine(PrivilegedCommandRequestsDirectory, $"{id:N}.json");

    public static string GetPrivilegedCommandResponsePath(Guid id) =>
        Path.Combine(PrivilegedCommandResponsesDirectory, $"{id:N}.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ServiceDirectory);
        Directory.CreateDirectory(PrivilegedCommandsDirectory);
        Directory.CreateDirectory(PrivilegedCommandRequestsDirectory);
        Directory.CreateDirectory(PrivilegedCommandResponsesDirectory);
    }

    /// <summary>
    /// Versions before 2.0.85 kept their data in folders named "CentricDeviceMonitor". The installer moves the shared
    /// folder; the dashboard moves each user's own folder (which the installer cannot reach), and the service copies
    /// whatever the installer could not move.
    /// </summary>
    public static void MoveFromOldFolderName(Environment.SpecialFolder parent) =>
        MoveFolder(Path.Combine(Environment.GetFolderPath(parent), OldFolderName), Path.Combine(Environment.GetFolderPath(parent), FolderName));

    /// <summary>Moves a folder; when the new one already exists, copies the files it lacks and removes the old one.</summary>
    internal static void MoveFolder(string oldPath, string newPath)
    {
        try
        {
            if (!Directory.Exists(oldPath))
            {
                return;
            }

            if (!Directory.Exists(newPath))
            {
                Directory.Move(oldPath, newPath);
                return;
            }

            foreach (string file in Directory.EnumerateFiles(oldPath, "*", SearchOption.AllDirectories))
            {
                CopyIfMissing(file, Path.Combine(newPath, Path.GetRelativePath(oldPath, file)));
            }

            Directory.Delete(oldPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Tried again at the next start; nothing is lost, the old folder stays until then.
        }
    }

    public static void MigrateLegacyCurrentUserData()
    {
        EnsureDirectories();

        // Versions before the background service kept devices and settings in the user's own folder.
        string legacyRoot = UserDirectory;

        if (!Directory.Exists(legacyRoot) ||
            string.Equals(legacyRoot, RootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CopyIfMissing(Path.Combine(legacyRoot, "devices.json"), DevicesFilePath);
        CopyIfMissing(Path.Combine(legacyRoot, "settings.json"), SettingsFilePath);

        string legacyLogs = Path.Combine(legacyRoot, "logs");
        if (Directory.Exists(legacyLogs))
        {
            foreach (string sourceFile in Directory.EnumerateFiles(legacyLogs, "*", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFileName(sourceFile), "application.log", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string targetFile = Path.Combine(LogsDirectory, Path.GetFileName(sourceFile));
                CopyIfMissing(sourceFile, targetFile);
            }
        }
    }

    private static void CopyIfMissing(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath) || File.Exists(targetPath))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(sourcePath, targetPath, overwrite: false);
    }
}
