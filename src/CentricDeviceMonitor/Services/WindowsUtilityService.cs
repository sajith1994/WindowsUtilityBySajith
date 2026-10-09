using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed class WindowsUtilityService
{
    private readonly PrivilegedCommandClient _privilegedCommandClient = new();

    public bool IsRunningAsAdministrator()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public void RestartApplicationAsAdministrator()
    {
        string executablePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("The application path could not be determined.");

        string arguments = "--elevated-restart";
        if (string.Equals(
                Path.GetFileNameWithoutExtension(executablePath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            string assemblyName = typeof(WindowsUtilityService).Assembly.GetName().Name
                ?? "CentricDeviceMonitor";
            string assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            arguments = $"\"{assemblyPath}\" --elevated-restart";
        }

        ProcessStartInfo startInfo = new(executablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = arguments
        };

        Process.Start(startInfo);
    }

    public void OpenDevicesAndPrinters() => StartProcess("control.exe", "/name Microsoft.DevicesAndPrinters");

    public void OpenTaskManager() => StartProcess("taskmgr.exe", string.Empty);

    public void OpenCommandPrompt(bool elevated) => LaunchConsole("cmd.exe", string.Empty, elevated);

    public void OpenPowerShell(bool elevated) => LaunchConsole("powershell.exe", "-NoExit", elevated);

    public void OpenDiskCleanupElevated() => LaunchConsole("cleanmgr.exe", string.Empty, elevated: true);

    public void OpenDiskPartElevated() => LaunchConsole("diskpart.exe", string.Empty, elevated: true);

    public void OpenDiskManagementElevated() => LaunchConsole("mmc.exe", "diskmgmt.msc", elevated: true);

    public void OpenStorageSenseSettings() => StartShell("ms-settings:storagepolicies");

    /// <summary>
    /// Opens the Windows Security page for Smart App Control.
    /// Requires Windows 11 22H2 (build 22621) or later; earlier builds have no such page.
    /// </summary>
    public void OpenSmartAppControlSettings()
    {
        if (!IsSmartAppControlAvailable())
        {
            throw new InvalidOperationException(
                "Smart App Control requires Windows 11 22H2 (build 22621) or later.");
        }

        StartShell("windowsdefender://smartappcontrol");
    }

    /// <summary>
    /// True when the running OS is new enough to expose Smart App Control.
    /// </summary>
    public static bool IsSmartAppControlAvailable() =>
        Environment.OSVersion.Platform == PlatformID.Win32NT
        && Environment.OSVersion.Version.Build >= 22621;

    public bool IsBootRecAvailable() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "bootrec.exe"));

    public async Task<string?> TryDetectBcdOsDriveAsync()
    {
        ProcessStartInfo startInfo = new("cmd.exe", "/d /c bcdedit | findstr /i osdevice")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start BCDEdit drive detection.");

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Match match = Regex.Match(output, @"partition\s*=\s*([A-Za-z]:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
        {
            return match.Groups[1].Value.ToUpperInvariant();
        }

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
        {
            throw new InvalidOperationException($"BCDEdit could not determine the Windows OS drive. {error.Trim()}");
        }

        return null;
    }

    public void RunCommandPromptCommand(string command, bool elevated)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            OpenCommandPrompt(elevated);
            return;
        }

        string scriptDirectory = Path.Combine(Path.GetTempPath(), "WindowsUtilityBySajith", "CommandRunner");
        Directory.CreateDirectory(scriptDirectory);
        string scriptPath = Path.Combine(scriptDirectory, $"cmd-{Guid.NewGuid():N}.cmd");

        // Two things cmd.exe gets wrong with the obvious approach:
        //
        // 1. Encoding.UTF8 writes a byte-order mark. cmd does not understand BOMs, so the first
        //    line became "´╗┐@echo off", which it reported as an unrecognised command and then
        //    ran with echo still on. A BOM-less writer plus an explicit code page fixes that and
        //    still lets the command contain non-ASCII paths.
        //
        // 2. A batch file that deletes itself with a plain "del" makes cmd print "The batch file
        //    cannot be found" when it tries to read the line after the deletion. The
        //    "(goto) 2>nul & del" idiom ends batch processing before the delete runs, so there is
        //    no next line for cmd to look for.
        string script =
            "@echo off\r\n" +
            "chcp 65001 >nul\r\n" +
            command.Trim() + "\r\n" +
            "(goto) 2>nul & del /q \"%~f0\"\r\n";

        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        LaunchConsole("cmd.exe", $"/d /k call \"{scriptPath}\"", elevated);
    }

    public void RunPowerShellCommand(string command, bool elevated)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            OpenPowerShell(elevated);
            return;
        }

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        LaunchConsole(
            "powershell.exe",
            $"-NoLogo -NoProfile -ExecutionPolicy Bypass -NoExit -EncodedCommand {encoded}",
            elevated);
    }

    public void ScheduleShutdown(int delayMinutes, bool forceCloseApplications) =>
        SchedulePowerAction(delayMinutes, forceCloseApplications, restart: false);

    public void ScheduleRestart(int delayMinutes, bool forceCloseApplications) =>
        SchedulePowerAction(delayMinutes, forceCloseApplications, restart: true);

    public void CancelScheduledPowerAction()
    {
        EnableShutdownPrivilege();
        if (!AbortSystemShutdownW(null))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "Windows could not cancel the pending shutdown/restart request.");
        }
    }

    public void OpenDeviceWebPage(string ipAddress) => StartShell($"http://{ipAddress}");

    public void OpenNetworkShare(string ipAddress) => StartProcess("explorer.exe", $@"\\{ipAddress}");

    public void OpenRemoteDesktop(string ipAddress) => StartProcess("mstsc.exe", $"/v:{ipAddress}");

    public async Task<string> GetPrintSpoolerStatusAsync()
    {
        try
        {
            ProcessStartInfo startInfo = new("sc.exe", "query Spooler")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to query the Print Spooler service.");

            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Match stateMatch = Regex.Match(output, @"STATE\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            if (!stateMatch.Success || !int.TryParse(stateMatch.Groups[1].Value, out int state))
            {
                return string.IsNullOrWhiteSpace(error) ? "Unknown" : "Unavailable";
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
        catch
        {
            return "Unavailable";
        }
    }

    public Task StartPrintSpoolerAsync() =>
        _privilegedCommandClient.ExecuteAsync(PrivilegedServiceActions.StartPrintSpooler);

    public Task StopPrintSpoolerAsync() =>
        _privilegedCommandClient.ExecuteAsync(PrivilegedServiceActions.StopPrintSpooler);

    public Task RestartPrintSpoolerAsync() =>
        _privilegedCommandClient.ExecuteAsync(PrivilegedServiceActions.RestartPrintSpooler);

    private static void SchedulePowerAction(int delayMinutes, bool forceCloseApplications, bool restart)
    {
        int normalizedMinutes = Math.Clamp(delayMinutes, 0, 120);
        uint timeoutSeconds = checked((uint)(normalizedMinutes * 60));
        EnableShutdownPrivilege();

        const uint ShutdownReasonMajorApplication = 0x00040000;
        const uint ShutdownReasonFlagPlanned = 0x80000000;
        uint reason = ShutdownReasonMajorApplication | ShutdownReasonFlagPlanned;
        string actionName = restart ? "restart" : "shutdown";
        string message = $"Windows Utility scheduled this Windows {actionName}.";

        if (!InitiateSystemShutdownExW(
                null,
                message,
                timeoutSeconds,
                forceCloseApplications,
                restart,
                reason))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Windows could not schedule the {actionName} request.");
        }
    }

    private static void EnableShutdownPrivilege()
    {
        const uint TokenAdjustPrivileges = 0x0020;
        const uint TokenQuery = 0x0008;
        const uint SePrivilegeEnabled = 0x00000002;
        const int ErrorNotAllAssigned = 1300;

        if (!OpenProcessToken(
                Process.GetCurrentProcess().Handle,
                TokenAdjustPrivileges | TokenQuery,
                out IntPtr tokenHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open the process token for shutdown privileges.");
        }

        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out Luid luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resolve the Windows shutdown privilege.");
            }

            TokenPrivileges privileges = new()
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };

            if (!AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to enable the Windows shutdown privilege.");
            }

            int lastError = Marshal.GetLastWin32Error();
            if (lastError == ErrorNotAllAssigned)
            {
                throw new Win32Exception(lastError, "The current account does not have the Windows shutdown privilege.");
            }
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitiateSystemShutdownExW(
        string? lpMachineName,
        string? lpMessage,
        uint dwTimeout,
        [MarshalAs(UnmanagedType.Bool)] bool bForceAppsClosed,
        [MarshalAs(UnmanagedType.Bool)] bool bRebootAfterShutdown,
        uint dwReason);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AbortSystemShutdownW(string? lpMachineName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out Luid lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private static void LaunchConsole(string executable, string arguments, bool elevated)
    {
        if (elevated)
        {
            Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            });
            return;
        }

        // The dashboard itself is elevated. Route normal launches through the Explorer shell
        // so technicians can intentionally open a standard-user console when Explorer is not elevated.
        try
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application")
                ?? throw new InvalidOperationException("The Windows shell automation interface is unavailable.");
            object shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("The Windows shell automation interface could not be created.");
            shellType.InvokeMember(
                "ShellExecute",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                new object?[]
                {
                    executable,
                    arguments,
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "open",
                    1
                });
            if (System.Runtime.InteropServices.Marshal.IsComObject(shell))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch
        {
            // Fallback: launch normally. On systems where Explorer itself is elevated this may
            // inherit the dashboard token, but the shortcut remains functional.
            Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            });
        }
    }

    private static void StartShell(string target)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private static void StartProcess(string fileName, string arguments)
    {
        Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
    }
}
