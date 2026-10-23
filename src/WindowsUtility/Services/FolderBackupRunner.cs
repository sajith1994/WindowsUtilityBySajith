using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using WindowsUtility.Models;

namespace WindowsUtility.Services;

public sealed record BackupRunResult(bool Succeeded, string Message);

/// <summary>
/// Copies a folder backup with robocopy, as the signed-in user (so only what they can read is copied, only to where
/// they can write): new and changed files only; nothing is deleted at the destination. Used by the status icon on
/// schedule and for live sync, and by "Run now" in the dashboard. One run of a backup at a time across processes.
/// </summary>
public static class FolderBackupRunner
{
    /// <summary>Runs the backup and records how it went in the store (whoever runs it: the status icon or "Run now").</summary>
    public static async Task<BackupRunResult> RunAsync(BackupSchedule schedule, BackupScheduleStore store, Action<string>? log, CancellationToken cancellationToken)
    {
        // The status icon and the dashboard may both try; the second one finds the lock file open and leaves it.
        // (A file, not a mutex: the run continues on other threads, and a mutex must be released by its own thread.)
        FileStream turn;
        try
        {
            string locks = Path.Combine(SharedDataPaths.UserDirectory, "locks");
            Directory.CreateDirectory(locks);
            turn = new FileStream(Path.Combine(locks, $"{schedule.Id:N}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return new BackupRunResult(false, "This backup is already running.");
        }

        await using FileStream held = turn;
        DateTime started = DateTime.UtcNow;
        store.UpdateState(new BackupScheduleState { Id = schedule.Id, LastRunUtc = started, Running = true, LastMessage = "Copying...", Stage = "Copying..." });
        BackupRunResult result;
        try
        {
            result = await RunUnrecordedAsync(schedule, store.Unprotect(schedule.ShareSecret), log, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            store.UpdateState(new BackupScheduleState { Id = schedule.Id, LastRunUtc = started, LastSucceeded = false, LastMessage = "Stopped before it finished." });
            throw;
        }

        store.UpdateState(new BackupScheduleState { Id = schedule.Id, LastRunUtc = started, LastSucceeded = result.Succeeded, LastMessage = result.Message });
        return result;
    }

    private static async Task<BackupRunResult> RunUnrecordedAsync(BackupSchedule schedule, string password, Action<string>? log, CancellationToken cancellationToken)
    {
        try
        {
            if (schedule.Destination.Length == 0)
            {
                return new BackupRunResult(false, "No destination folder is set.");
            }

            if (schedule.Destination.StartsWith(@"\\", StringComparison.Ordinal) && schedule.ShareUser.Length > 0)
            {
                await ConnectShareAsync(schedule.Destination, schedule.ShareUser, password, cancellationToken);
            }

            return await CopyAsync(schedule, log, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ApplicationLogService.WriteException($"Folder backup: {schedule.Name}", exception);
            return new BackupRunResult(false, exception.Message);
        }
    }

    /// <summary>The folder the copy goes into: the destination plus the backed-up folder's name ("D" for a whole drive).</summary>
    public static string TargetFolder(BackupSchedule schedule)
    {
        string source = schedule.SourceFolder.TrimEnd('\\');
        string leaf = Path.GetFileName(source);
        return Path.Combine(schedule.Destination, leaf.Length > 0 ? leaf : source.Replace(":", string.Empty).Trim('\\'));
    }

    private static async Task<BackupRunResult> CopyAsync(BackupSchedule schedule, Action<string>? log, CancellationToken cancellationToken)
    {
        string source = schedule.SourceFolder.TrimEnd('\\');
        if (!Directory.Exists(source))
        {
            return new BackupRunResult(false, $"The folder {source} was not found.");
        }

        string target = TargetFolder(schedule);
        Directory.CreateDirectory(target);
        log?.Invoke($"Copying {source} to {target} (new and changed files; nothing is deleted there).");

        // /E all folders, /XO only files newer than the copy, /R:1 one retry, /XJ no junctions (they loop),
        // /NP /NFL /NDL /NJH the summary only.
        ProcessStartInfo startInfo = new("robocopy.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { source, target, "/E", "/COPY:DAT", "/DCOPY:DAT", "/XO", "/R:1", "/W:2", "/XJ", "/NP", "/NFL", "/NDL", "/NJH" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("robocopy could not be started.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        string summary = Summarize(await output);
        // robocopy exit codes: bits 0-2 report copies and extras, 8 and 16 mean failures.
        bool ok = process.ExitCode < 8;
        string text = ok ? $"Folder copied: {summary}" : $"Some files could not be copied ({summary}). {(await error).Trim()}".Trim();
        log?.Invoke(text);
        return new BackupRunResult(ok, text);
    }

    /// <summary>Signs in to a network share (\\server\share\...) with a name and password for this session.</summary>
    public static async Task ConnectShareAsync(string path, string user, string password, CancellationToken cancellationToken)
    {
        string[] parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) || parts.Length < 2)
        {
            throw new InvalidOperationException(@"A network share looks like \\server\share\folder.");
        }

        string share = $@"\\{parts[0]}\{parts[1]}";
        ProcessStartInfo startInfo = new("net.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { "use", share, password, $"/user:{user}", "/persistent:no" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("net.exe could not be started.");
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken) + await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        // 1219: already connected with other credentials; that connection is used.
        if (process.ExitCode != 0 && !output.Contains("1219", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Windows could not connect to {share}: {output.Trim()}");
        }
    }

    /// <summary>"1234 files, 56 copied, 1176 unchanged, 2 failed, 120.5 mB copied" from robocopy's summary table.</summary>
    internal static string Summarize(string robocopyOutput)
    {
        Match files = Regex.Match(robocopyOutput, @"Files\s*:\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)");
        Match bytes = Regex.Match(robocopyOutput, @"Bytes\s*:\s*([\d.]+\s*[kmgt]?)\s+([\d.]+\s*[kmgt]?)", RegexOptions.IgnoreCase);
        if (!files.Success)
        {
            return "no summary from robocopy";
        }

        string copied = bytes.Success ? $", {bytes.Groups[2].Value.Trim()}B copied" : string.Empty;
        return $"{files.Groups[1].Value} files, {files.Groups[2].Value} copied, {files.Groups[3].Value} unchanged, {files.Groups[5].Value} failed{copied}";
    }

    /// <summary>
    /// What is wrong with a folder backup before it is saved; null when it is fine. The destination may not be inside
    /// the folder (it would copy itself forever).
    /// </summary>
    public static string? Validate(BackupSchedule schedule)
    {
        string source = schedule.SourceFolder.Trim().TrimEnd('\\');
        string destination = schedule.Destination.Trim().TrimEnd('\\');
        if (schedule.Name.Trim().Length == 0)
        {
            return "Give the backup a name.";
        }

        if (source.Length == 0 || !Directory.Exists(source))
        {
            return "The folder to back up was not found.";
        }

        if (destination.Length == 0)
        {
            return "Choose where to save the backup.";
        }

        if (destination.Equals(source, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return "The destination cannot be inside the folder it backs up.";
        }

        if (schedule.Frequency == BackupFrequency.Weekly && schedule.WeekDays.Count == 0)
        {
            return "Tick at least one day of the week.";
        }

        return null;
    }
}
