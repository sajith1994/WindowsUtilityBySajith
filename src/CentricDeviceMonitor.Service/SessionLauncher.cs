using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.ServiceHost;

/// <summary>
/// The installer closes the dashboard and the tray icon in every session before replacing them. Before an update the
/// service notes which sessions had them open; when the new service starts it opens them again in those sessions, as
/// the signed-in user (never as SYSTEM) and without administrator rights.
/// </summary>
internal static class SessionLauncher
{
    public const string DashboardProcessName = "WindowsUtilityBySajith";
    public const string TrayProcessName = "CentricDeviceMonitorTray";
    private static readonly TimeSpan MarkerLifetime = TimeSpan.FromMinutes(20);

    private static string MarkerPath => Path.Combine(SharedDataPaths.RootDirectory, "update-relaunch-sessions.json");

    /// <summary>Written by 2.0.77 and older services before an update started from another PC.</summary>
    private static string LegacyMarkerPath => Path.Combine(SharedDataPaths.RootDirectory, "remote-update-relaunch.flag");

    internal sealed class RelaunchMarker
    {
        public DateTime WrittenUtc { get; set; }
        public List<int> DashboardSessions { get; set; } = new();
        public List<int> TraySessions { get; set; } = new();
    }

    /// <summary>Called just before the installer runs.</summary>
    public static void RememberOpenApps()
    {
        try
        {
            RelaunchMarker marker = new()
            {
                WrittenUtc = DateTime.UtcNow,
                DashboardSessions = SessionsRunning(DashboardProcessName),
                TraySessions = SessionsRunning(TrayProcessName)
            };
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ServiceLog.WriteException("Update relaunch marker", exception);
        }
    }

    /// <summary>Runs once when the service starts; does nothing unless an update just happened.</summary>
    public static async Task RelaunchAfterUpdate()
    {
        try
        {
            RelaunchMarker? marker = ReadMarker();
            if (marker is null)
            {
                return;
            }

            // Give the installer a moment to finish its last steps.
            await Task.Delay(TimeSpan.FromSeconds(5));
            string folder = AppContext.BaseDirectory;
            foreach (int session in marker.TraySessions.Distinct().Where(session => !IsRunningIn(TrayProcessName, session)))
            {
                Launch(session, Path.Combine(folder, TrayProcessName + ".exe"), string.Empty);
            }

            foreach (int session in marker.DashboardSessions.Distinct().Where(session => !IsRunningIn(DashboardProcessName, session)))
            {
                Launch(session, Path.Combine(folder, DashboardProcessName + ".exe"), string.Empty);
            }
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("Update relaunch", exception);
        }
    }

    private static RelaunchMarker? ReadMarker()
    {
        RelaunchMarker? marker = null;
        if (File.Exists(MarkerPath))
        {
            try
            {
                marker = JsonSerializer.Deserialize<RelaunchMarker>(File.ReadAllText(MarkerPath));
            }
            catch (JsonException)
            {
            }

            File.Delete(MarkerPath);
        }
        else if (File.Exists(LegacyMarkerPath))
        {
            // An update from 2.0.77 or older only noted that the dashboard was open: reopen it for the person at the
            // screen, and the tray icon for everyone signed in.
            marker = new RelaunchMarker
            {
                WrittenUtc = File.GetLastWriteTimeUtc(LegacyMarkerPath),
                DashboardSessions = new List<int> { (int)WTSGetActiveConsoleSessionId() },
                TraySessions = UserSessions()
            };
            File.Delete(LegacyMarkerPath);
        }

        return marker is not null && DateTime.UtcNow - marker.WrittenUtc < MarkerLifetime ? marker : null;
    }

    private static List<int> SessionsRunning(string processName) =>
        Process.GetProcessesByName(processName).Select(process =>
        {
            using (process)
            {
                return process.SessionId;
            }
        }).Where(session => session > 0).Distinct().ToList();

    private static bool IsRunningIn(string processName, int session) => SessionsRunning(processName).Contains(session);

    /// <summary>Sessions with someone signed in (active or disconnected).</summary>
    private static List<int> UserSessions()
    {
        List<int> sessions = new();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr buffer, out int count))
        {
            return sessions;
        }

        try
        {
            int size = Marshal.SizeOf<WtsSessionInfo>();
            for (int index = 0; index < count; index++)
            {
                WtsSessionInfo session = Marshal.PtrToStructure<WtsSessionInfo>(buffer + index * size);
                if (session.SessionId > 0 && session.State is 0 or 4)
                {
                    sessions.Add(session.SessionId);
                }
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return sessions;
    }

    /// <summary>Starts a program on the desktop of a session, as the user signed in to it (their normal rights).</summary>
    internal static bool Launch(int sessionId, string executable, string arguments)
    {
        if (!File.Exists(executable))
        {
            return false;
        }

        IntPtr userToken = IntPtr.Zero, primaryToken = IntPtr.Zero, environment = IntPtr.Zero;
        try
        {
            if (!WTSQueryUserToken((uint)sessionId, out userToken))
            {
                ServiceLog.Write("Update relaunch", $"No signed-in user in session {sessionId} ({new Win32Exception().Message}).");
                return false;
            }

            if (!DuplicateTokenEx(userToken, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out primaryToken))
            {
                throw new Win32Exception();
            }

            CreateEnvironmentBlock(out environment, primaryToken, false);
            StartupInfo startup = new() { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default" };
            // CreateProcessAsUser may write to the command line, so it gets its own buffer.
            System.Text.StringBuilder commandLine = new($"\"{executable}\" {arguments}".Trim());
            if (!CreateProcessAsUser(primaryToken, null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    0x00000400 /* CREATE_UNICODE_ENVIRONMENT */, environment, Path.GetDirectoryName(executable), ref startup, out ProcessInformation process))
            {
                throw new Win32Exception();
            }

            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
            ServiceLog.Write("Update relaunch", $"Reopened {Path.GetFileName(executable)} in session {sessionId}.");
            return true;
        }
        catch (Win32Exception exception)
        {
            ServiceLog.Write("Update relaunch", $"Could not reopen {Path.GetFileName(executable)} in session {sessionId}: {exception.Message}");
            return false;
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            if (primaryToken != IntPtr.Zero)
            {
                CloseHandle(primaryToken);
            }

            if (userToken != IntPtr.Zero)
            {
                CloseHandle(userToken);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, System.Text.StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
