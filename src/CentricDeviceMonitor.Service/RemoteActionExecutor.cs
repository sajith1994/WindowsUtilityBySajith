using System.Diagnostics;
using System.Runtime.InteropServices;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.ServiceHost;

/// <summary>
/// Carries out actions asked for by the PC monitoring this one (only when "Allow actions" is on). Runs in the
/// LocalSystem service: restart and shutdown use shutdown.exe with a visible countdown and message, the message
/// and lock act on the signed-in users' sessions, cleanup removes temporary files older than a day, the
/// Windows Update scan asks the update orchestrator to check for updates, and a restore point is made through
/// System Restore.
/// </summary>
internal static class RemoteActionExecutor
{
    public static async Task<RemoteActionResult> RunAsync(RemoteActionRequest request)
    {
        string from = string.IsNullOrWhiteSpace(request.RequestedBy) ? "the monitoring PC" : request.RequestedBy;
        string message = (request.Message ?? string.Empty).Trim();
        if (message.Length > 400)
        {
            message = message[..400];
        }

        switch (request.Action)
        {
            case RemoteActions.Message:
                if (message.Length == 0)
                {
                    return Fail("The message is empty.");
                }

                int shown = ForEachUserSession(session => WTSSendMessage(IntPtr.Zero, session, Title(from), Title(from).Length * 2,
                    message, message.Length * 2, 0x40 /* MB_ICONINFORMATION */, 0, out _, false));
                return shown > 0 ? Ok($"Message shown to {shown} signed-in user{(shown == 1 ? "" : "s")}.") : Fail("Nobody is signed in to show the message to.");

            case RemoteActions.Lock:
                int locked = ForEachUserSession(session => WTSDisconnectSession(IntPtr.Zero, session, false), activeOnly: true);
                return locked > 0 ? Ok("The screen was locked.") : Fail("Nobody is actively using the PC, so there was nothing to lock.");

            case RemoteActions.Restart or RemoteActions.Shutdown:
            {
                int minutes = Math.Clamp(request.DelayMinutes, 0, 60);
                string verb = request.Action == RemoteActions.Restart ? "restart" : "shut down";
                string note = message.Length > 0
                    ? message
                    : $"This PC will {verb} in {minutes} minute{(minutes == 1 ? "" : "s")} ({from}). Please save your work.";
                (int exit, string output) = await RunAsync("shutdown.exe", request.Action == RemoteActions.Restart ? "/r" : "/s",
                    "/t", (minutes * 60).ToString(), "/c", note.Length > 500 ? note[..500] : note, "/d", "p:0:0");
                return exit == 0
                    ? Ok(minutes == 0 ? $"The PC is {(request.Action == RemoteActions.Restart ? "restarting" : "shutting down")} now." : $"The PC will {verb} in {minutes} minute{(minutes == 1 ? "" : "s")}; the user sees a countdown.")
                    : Fail(exit == 1190 ? "A restart or shutdown is already scheduled. Cancel it first." : $"shutdown.exe failed ({exit}): {output}");
            }

            case RemoteActions.CancelShutdown:
            {
                (int exit, _) = await RunAsync("shutdown.exe", "/a");
                return exit == 0 ? Ok("The scheduled restart or shutdown was cancelled.") : Ok("No restart or shutdown was scheduled.");
            }

            case RemoteActions.ClearTemp:
                return await Task.Run(ClearTemporaryFiles);

            case RemoteActions.RestorePoint:
            {
                Task<RemoteActionResult> create = Task.Run(() => RecoveryStatusProvider.CreateRestorePoint(from));
                return await Task.WhenAny(create, Task.Delay(TimeSpan.FromSeconds(150))) == create
                    ? await create
                    : Ok("Windows is still creating the restore point (it can take a few minutes); it appears on the tile when done.");
            }

            case RemoteActions.UpdateScan:
            {
                (int exit, string output) = await RunAsync("UsoClient.exe", "StartScan");
                return exit == 0 ? Ok("Windows Update is checking for updates. New updates appear in Windows Update and on the tile within the hour.") : Fail($"Windows Update could not be started ({exit}): {output}");
            }

            default:
                return Fail($"Unknown action \"{request.Action}\".");
        }
    }

    private static string Title(string from) => $"Message from {from}";

    /// <summary>Deletes temporary files older than a day from Windows' and every user's temp folder.</summary>
    private static RemoteActionResult ClearTemporaryFiles()
    {
        List<string> folders = new() { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") };
        string users = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Users");
        if (Directory.Exists(users))
        {
            folders.AddRange(Directory.EnumerateDirectories(users)
                .Select(profile => Path.Combine(profile, "AppData", "Local", "Temp"))
                .Where(Directory.Exists));
        }

        DateTime cutoff = DateTime.Now.AddDays(-1);
        long freed = 0;
        int files = 0;
        int skipped = 0;
        foreach (string folder in folders)
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in entries)
            {
                try
                {
                    FileInfo info = new(file);
                    if (info.LastWriteTime > cutoff)
                    {
                        continue;
                    }

                    long size = info.Length;
                    info.Attributes = FileAttributes.Normal;
                    info.Delete();
                    freed += size;
                    files++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    skipped++; // In use: left alone.
                }
            }
        }

        return Ok($"Removed {files:N0} temporary files ({freed / 1024d / 1024:N0} MB freed){(skipped > 0 ? $"; {skipped:N0} in use were left alone" : string.Empty)}.");
    }

    /// <summary>Runs the action once per signed-in session; returns how many sessions it succeeded for.</summary>
    private static int ForEachUserSession(Func<int, bool> action, bool activeOnly = false)
    {
        int done = 0;
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr buffer, out int count))
        {
            return 0;
        }

        try
        {
            int size = Marshal.SizeOf<WtsSessionInfo>();
            for (int index = 0; index < count; index++)
            {
                WtsSessionInfo session = Marshal.PtrToStructure<WtsSessionInfo>(buffer + index * size);
                if (session.State != 0 && (activeOnly || session.State != 4))
                {
                    continue; // 0 = active, 4 = disconnected
                }

                if (!HasUser(session.SessionId))
                {
                    continue;
                }

                if (action(session.SessionId))
                {
                    done++;
                }
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return done;
    }

    private static bool HasUser(int sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, 5 /* WTSUserName */, out IntPtr value, out _))
        {
            return false;
        }

        try
        {
            return !string.IsNullOrEmpty(Marshal.PtrToStringUni(value));
        }
        finally
        {
            WTSFreeMemory(value);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, params string[] arguments)
    {
        ProcessStartInfo info = new(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"{file} could not be started.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, ((await output) + (await error)).Trim());
    }

    private static RemoteActionResult Ok(string message) => new() { Ok = true, Message = message };

    private static RemoteActionResult Fail(string message) => new() { Ok = false, Message = message };

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSSendMessage(IntPtr server, int sessionId, string title, int titleLength, string message, int messageLength,
        int style, int timeout, out int response, bool wait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSDisconnectSession(IntPtr server, int sessionId, bool wait);
}
