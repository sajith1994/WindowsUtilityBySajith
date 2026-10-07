using System.IO;

namespace CentricDeviceMonitor.Services;

public static class SharedDataPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CentricDeviceMonitor");

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

    public static string DashboardTrayReadyFilePath => Path.Combine(ServiceDirectory, "dashboard-tray.ready");

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

    public static void MigrateLegacyCurrentUserData()
    {
        EnsureDirectories();

        string legacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CentricDeviceMonitor");

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
