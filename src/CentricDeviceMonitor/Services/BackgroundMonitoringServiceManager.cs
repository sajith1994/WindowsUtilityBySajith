using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CentricDeviceMonitor.Services;

public sealed record BackgroundMonitoringServiceInfo(
    bool Exists,
    bool IsRunning,
    string Status,
    string StartupType,
    string Account);

public sealed class BackgroundMonitoringServiceManager
{
    public const string ServiceName = "CentricDeviceMonitorService";
    public const string DisplayName = "Windows Utility Background Service";

    public async Task<BackgroundMonitoringServiceInfo> GetInfoAsync()
    {
        ProcessResult query = await RunScAsync($"query \"{ServiceName}\"");
        if (query.ExitCode != 0)
        {
            return new BackgroundMonitoringServiceInfo(
                false,
                false,
                "Not installed",
                "Not installed",
                "Not available");
        }

        string status = ParseState(query.Output);
        ProcessResult configuration = await RunScAsync($"qc \"{ServiceName}\"");
        string account = ParseServiceStartName(configuration.Output);
        string startupType = ParseStartupType(configuration.Output);

        if (string.Equals(startupType, "Automatic", StringComparison.OrdinalIgnoreCase) && IsDelayedAutoStartEnabled())
        {
            startupType = "Automatic (Delayed Start)";
        }

        return new BackgroundMonitoringServiceInfo(
            true,
            string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase),
            status,
            startupType,
            account);
    }

    public async Task RestartAsync()
    {
        ProcessStartInfo startInfo = new("powershell.exe")
        {
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Restart-Service -Name '{ServiceName}' -Force -ErrorAction Stop\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The service restart command could not be started.");

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The background service restart ended with exit code {process.ExitCode}.");
        }
    }

    public Task EnableAndStartAsync()
    {
        string serviceRegistryPath = $@"HKLM:\SYSTEM\CurrentControlSet\Services\{ServiceName}";
        string script =
            "$ErrorActionPreference = 'Stop'; " +
            $"$service = Get-Service -Name '{ServiceName}' -ErrorAction Stop; " +
            $"Set-Service -Name '{ServiceName}' -StartupType Automatic -ErrorAction Stop; " +
            $"New-ItemProperty -Path '{serviceRegistryPath}' -Name DelayedAutostart -PropertyType DWord -Value 1 -Force -ErrorAction Stop | Out-Null; " +
            $"if ((Get-Service -Name '{ServiceName}').Status -ne 'Running') {{ Start-Service -Name '{ServiceName}' -ErrorAction Stop }}";
        return RunElevatedPowerShellAsync(script, "enable and start");
    }

    public Task DisableAndStopAsync()
    {
        string serviceRegistryPath = $@"HKLM:\SYSTEM\CurrentControlSet\Services\{ServiceName}";
        string script =
            "$ErrorActionPreference = 'Stop'; " +
            $"$service = Get-Service -Name '{ServiceName}' -ErrorAction Stop; " +
            $"if ($service.Status -ne 'Stopped') {{ Stop-Service -Name '{ServiceName}' -Force -ErrorAction Stop }}; " +
            $"Set-Service -Name '{ServiceName}' -StartupType Disabled -ErrorAction Stop; " +
            $"New-ItemProperty -Path '{serviceRegistryPath}' -Name DelayedAutostart -PropertyType DWord -Value 0 -Force -ErrorAction Stop | Out-Null";
        return RunElevatedPowerShellAsync(script, "disable and stop");
    }

    public void OpenServiceLog()
    {
        SharedDataPaths.EnsureDirectories();
        if (!File.Exists(SharedDataPaths.ServiceLogFilePath))
        {
            File.WriteAllText(
                SharedDataPaths.ServiceLogFilePath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Service log has not recorded any entries yet.{Environment.NewLine}");
        }

        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{SharedDataPaths.ServiceLogFilePath}\"")
        {
            UseShellExecute = true
        });
    }

    private static async Task<ProcessResult> RunScAsync(string arguments)
    {
        try
        {
            ProcessStartInfo startInfo = new("sc.exe", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to query the Windows Service Control Manager.");

            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new ProcessResult(process.ExitCode, output, error);
        }
        catch
        {
            return new ProcessResult(-1, string.Empty, string.Empty);
        }
    }

    private static async Task RunElevatedPowerShellAsync(string script, string operation)
    {
        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        ProcessStartInfo startInfo = new("powershell.exe")
        {
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The service {operation} command could not be started.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The background service {operation} command ended with exit code {process.ExitCode}.");
        }
    }

    private static string ParseState(string output)
    {
        Match match = Regex.Match(output, @"STATE\s*:\s*(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int state))
        {
            return "Unknown";
        }

        return state switch
        {
            1 => "Stopped",
            2 => "Starting",
            3 => "Stopping",
            4 => "Running",
            5 => "Continuing",
            6 => "Pausing",
            7 => "Paused",
            _ => "Unknown"
        };
    }

    private static string ParseStartupType(string output)
    {
        Match match = Regex.Match(output, @"START_TYPE\s*:\s*(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int startType))
        {
            return "Unknown";
        }

        return startType switch
        {
            2 => "Automatic",
            3 => "Manual",
            4 => "Disabled",
            _ => "Other"
        };
    }

    private static string ParseServiceStartName(string output)
    {
        Match match = Regex.Match(output, @"SERVICE_START_NAME\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (!match.Success)
        {
            return "Unknown";
        }

        string account = match.Groups[1].Value.Trim();
        return string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase)
            ? "LocalSystem"
            : account;
    }

    private static bool IsDelayedAutoStartEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            object? value = key?.GetValue("DelayedAutostart");
            return value is int intValue && intValue == 1;
        }
        catch
        {
            return false;
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
