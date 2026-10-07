using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace CentricDeviceMonitor.Services;

public sealed class WindowsStartupService
{
    public const string ScheduledTaskName = "Windows Utility by Sajith Dashboard";
    private const string LegacyScheduledTaskName = "Centric Device Monitor Dashboard";
    private const string LegacyRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "CentricDeviceMonitor";

    public bool IsEnabled()
    {
        try
        {
            ProcessStartInfo startInfo = new("schtasks.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("/Query");
            startInfo.ArgumentList.Add("/TN");
            startInfo.ArgumentList.Add(ScheduledTaskName);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to query the Windows startup task.");
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        RemoveLegacyRegistryStartup();

        if (enabled)
        {
            CreateOrUpdateElevatedTask();
        }
        else
        {
            DeleteTask();
        }
    }

    private static void CreateOrUpdateElevatedTask()
    {
        DeleteScheduledTaskByName(LegacyScheduledTaskName);
        (string executablePath, string arguments) = ResolveExecutableAndArguments();
        string taskCommand = string.IsNullOrWhiteSpace(arguments)
            ? QuoteForTask(executablePath)
            : $"{QuoteForTask(executablePath)} {arguments}";

        string currentUser = WindowsIdentity.GetCurrent().Name;
        if (string.IsNullOrWhiteSpace(currentUser))
        {
            throw new InvalidOperationException("The current Windows account could not be determined.");
        }

        ProcessStartInfo startInfo = new("schtasks.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("/Create");
        startInfo.ArgumentList.Add("/TN");
        startInfo.ArgumentList.Add(ScheduledTaskName);
        startInfo.ArgumentList.Add("/TR");
        startInfo.ArgumentList.Add(taskCommand);
        startInfo.ArgumentList.Add("/SC");
        startInfo.ArgumentList.Add("ONLOGON");
        startInfo.ArgumentList.Add("/RU");
        startInfo.ArgumentList.Add(currentUser);
        startInfo.ArgumentList.Add("/RL");
        startInfo.ArgumentList.Add("HIGHEST");
        startInfo.ArgumentList.Add("/IT");
        startInfo.ArgumentList.Add("/F");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to create the elevated Windows startup task.");

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                $"Windows could not create the elevated startup task. {detail}".Trim());
        }
    }

    private static void DeleteTask()
    {
        DeleteScheduledTaskByName(ScheduledTaskName);
        DeleteScheduledTaskByName(LegacyScheduledTaskName);
    }

    private static void DeleteScheduledTaskByName(string taskName)
    {
        ProcessStartInfo startInfo = new("schtasks.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/Delete");
        startInfo.ArgumentList.Add("/TN");
        startInfo.ArgumentList.Add(taskName);
        startInfo.ArgumentList.Add("/F");

        using Process? process = Process.Start(startInfo);
        process?.WaitForExit();
    }

    private static (string ExecutablePath, string Arguments) ResolveExecutableAndArguments()
    {
        string processPath = Environment.ProcessPath ?? string.Empty;

        if (string.IsNullOrWhiteSpace(processPath))
        {
            processPath = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("The application path could not be determined.");
        }

        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            string assemblyName = typeof(WindowsStartupService).Assembly.GetName().Name
                ?? "CentricDeviceMonitor";
            string assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            return (processPath, $"{QuoteForTask(assemblyPath)} --startup");
        }

        return (processPath, "--startup");
    }

    private static string QuoteForTask(string value) => $"\"{value}\"";

    private static void RemoveLegacyRegistryStartup()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(LegacyRegistryPath, writable: true);
            key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch
        {
            // The scheduled task is the authoritative startup mechanism in 1.8.4+.
        }
    }
}
