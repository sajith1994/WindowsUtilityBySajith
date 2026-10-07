using System.Diagnostics;
using System.IO;
using System.Text;

namespace CentricDeviceMonitor.Services;

public sealed record UserApplicationTerminationEntry(
    string Application,
    int ProcessId,
    string Result,
    string WindowTitle,
    string ExecutablePath,
    string Details);

public sealed record UserApplicationTerminationResult(
    int ApplicationsEnded,
    int ClosedNormally,
    int ForceTerminated,
    int Failed,
    int SystemProcessesExcluded,
    IReadOnlyList<UserApplicationTerminationEntry> Entries,
    string LogFilePath);

public sealed class UserApplicationTerminationService
{
    public static string LogFilePath => Path.Combine(
        SharedDataPaths.LogsDirectory,
        "application-termination.csv");

    public Task<UserApplicationTerminationResult> TerminateVisibleUserApplicationsAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => TerminateVisibleUserApplications(cancellationToken), cancellationToken);

    private static UserApplicationTerminationResult TerminateVisibleUserApplications(
        CancellationToken cancellationToken)
    {
        int currentProcessId = Environment.ProcessId;
        int currentSessionId = Process.GetCurrentProcess().SessionId;
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        int closedNormally = 0;
        int forceTerminated = 0;
        int failed = 0;
        int systemExcluded = 0;
        List<UserApplicationTerminationEntry> entries = new();

        Process[] processes = Process.GetProcesses()
            .OrderBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (Process process in processes)
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (process.Id == currentProcessId || process.HasExited)
                    {
                        continue;
                    }

                    if (process.SessionId != currentSessionId)
                    {
                        continue;
                    }

                    string windowTitle = SafeGet(() => process.MainWindowTitle) ?? string.Empty;
                    IntPtr mainWindowHandle = SafeGet(() => process.MainWindowHandle);
                    if (mainWindowHandle == IntPtr.Zero && string.IsNullOrWhiteSpace(windowTitle))
                    {
                        continue;
                    }

                    string executablePath = TryGetExecutablePath(process);
                    if (string.IsNullOrWhiteSpace(executablePath))
                    {
                        entries.Add(new UserApplicationTerminationEntry(
                            process.ProcessName,
                            process.Id,
                            "Skipped",
                            windowTitle,
                            string.Empty,
                            "Executable path could not be verified, so the process was skipped for safety."));
                        continue;
                    }

                    if (IsWindowsSystemExecutable(executablePath, windowsDirectory))
                    {
                        systemExcluded++;
                        continue;
                    }

                    string applicationName = GetApplicationName(process, executablePath);
                    bool closeRequested = false;
                    try
                    {
                        closeRequested = process.CloseMainWindow();
                    }
                    catch
                    {
                    }

                    if (closeRequested && WaitForExit(process, 2500))
                    {
                        closedNormally++;
                        entries.Add(new UserApplicationTerminationEntry(
                            applicationName,
                            process.Id,
                            "Closed normally",
                            windowTitle,
                            executablePath,
                            "The application responded to the normal close request."));
                        continue;
                    }

                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            if (!WaitForExit(process, 3000))
                            {
                                throw new InvalidOperationException("The process did not exit after the termination request.");
                            }
                        }

                        forceTerminated++;
                        entries.Add(new UserApplicationTerminationEntry(
                            applicationName,
                            process.Id,
                            "Force terminated",
                            windowTitle,
                            executablePath,
                            closeRequested
                                ? "The application did not close within 2.5 seconds and was force terminated."
                                : "The application did not accept a normal close request and was force terminated."));
                    }
                    catch (Exception exception)
                    {
                        failed++;
                        entries.Add(new UserApplicationTerminationEntry(
                            applicationName,
                            process.Id,
                            "Failed",
                            windowTitle,
                            executablePath,
                            exception.Message));
                    }
                }
                catch (Exception exception)
                {
                    failed++;
                    entries.Add(new UserApplicationTerminationEntry(
                        SafeGet(() => process.ProcessName) ?? "Unknown",
                        SafeGet(() => process.Id),
                        "Failed",
                        string.Empty,
                        string.Empty,
                        exception.Message));
                }
            }
        }

        UserApplicationTerminationResult result = new(
            closedNormally + forceTerminated,
            closedNormally,
            forceTerminated,
            failed,
            systemExcluded,
            entries,
            LogFilePath);

        AppendLog(result);
        return result;
    }

    private static bool WaitForExit(Process process, int milliseconds)
    {
        try
        {
            return process.HasExited || process.WaitForExit(milliseconds);
        }
        catch
        {
            return false;
        }
    }

    private static string TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsWindowsSystemExecutable(string executablePath, string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(windowsDirectory))
        {
            return false;
        }

        try
        {
            string normalizedExecutable = Path.GetFullPath(executablePath);
            string normalizedWindows = Path.GetFullPath(windowsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return normalizedExecutable.StartsWith(normalizedWindows, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static string GetApplicationName(Process process, string executablePath)
    {
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(executablePath).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description.Trim();
            }
        }
        catch
        {
        }

        return SafeGet(() => process.ProcessName) ?? Path.GetFileNameWithoutExtension(executablePath);
    }

    private static T SafeGet<T>(Func<T> accessor)
    {
        try
        {
            return accessor();
        }
        catch
        {
            return default!;
        }
    }

    private static void AppendLog(UserApplicationTerminationResult result)
    {
        try
        {
            SharedDataPaths.EnsureDirectories();
            bool writeHeader = !File.Exists(LogFilePath) || new FileInfo(LogFilePath).Length == 0;
            using StreamWriter writer = new(LogFilePath, append: true, Encoding.UTF8);
            if (writeHeader)
            {
                writer.WriteLine("RunAt,Application,ProcessId,Result,WindowTitle,ExecutablePath,Details");
            }

            string runAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (UserApplicationTerminationEntry entry in result.Entries)
            {
                writer.WriteLine(string.Join(",",
                    Csv(runAt),
                    Csv(entry.Application),
                    entry.ProcessId.ToString(),
                    Csv(entry.Result),
                    Csv(entry.WindowTitle),
                    Csv(entry.ExecutablePath),
                    Csv(entry.Details)));
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Application termination log", exception);
        }
    }

    private static string Csv(string value) =>
        $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
}
