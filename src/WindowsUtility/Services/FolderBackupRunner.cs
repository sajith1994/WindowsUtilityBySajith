using System.Diagnostics;
using System.IO;
using WindowsUtility.Models;

namespace WindowsUtility.Services;

public sealed record BackupRunResult(bool Succeeded, string Message);

/// <summary>
/// Copies a folder backup as the signed-in user (so only what they can read is copied, only to where they can write).
/// The copy mirrors the folder; what it replaces or removes is kept, dated, in the copy's history (see
/// <see cref="FolderBackupHistory"/>). Used by the status icon on schedule and for live sync, and by "Run now" in the
/// dashboard. One run of a backup at a time across processes.
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
        log?.Invoke($"Copying {source} to {target}; replaced and removed files go to its {FolderBackupHistory.HistoryFolderName} folder.");
        CopyOutcome outcome = await Task.Run(() => Copy(source, target, schedule.HistoryDays, DateTime.UtcNow, cancellationToken), cancellationToken);
        string text = outcome.Describe();
        log?.Invoke(text);
        return new BackupRunResult(outcome.Failed == 0, text);
    }

    internal sealed record CopyOutcome(int Total, int Copied, int Unchanged, int Failed, int Removed, long BytesCopied, string FirstError)
    {
        public string Describe()
        {
            string summary = $"{Total} files, {Copied} copied, {Unchanged} unchanged" + (Removed > 0 ? $", {Removed} replaced or removed (kept in the history)" : string.Empty)
                + $", {FormatBytes(BytesCopied)} copied";
            return Failed == 0 ? $"Folder copied: {summary}." : $"{Failed} file{(Failed == 1 ? "" : "s")} could not be copied ({FirstError}); {summary}.";
        }
    }

    /// <summary>
    /// Makes the copy mirror the folder: new and changed files are copied (the replaced copy moves to this run's
    /// history folder), files no longer in the folder move there too, and a list of every file as it is now is
    /// written for "restore as of". Files in use are skipped and counted. Junctions and symbolic links are not followed.
    /// </summary>
    internal static CopyOutcome Copy(string source, string target, int historyDays, DateTime nowUtc, CancellationToken cancellationToken)
    {
        string history = FolderBackupHistory.RunFolder(target, nowUtc);
        int copied = 0, unchanged = 0, failed = 0, removed = 0;
        long bytes = 0;
        string firstError = string.Empty;
        List<BackupFileVersion> manifest = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (FileInfo file in EnumerateFiles(new DirectoryInfo(source)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file.FullName);
            seen.Add(relative);
            string copy = Path.Combine(target, relative);
            try
            {
                FileInfo existing = new(copy);
                if (existing.Exists && FolderBackupHistory.SameFile(file.Length, file.LastWriteTimeUtc, existing))
                {
                    unchanged++;
                }
                else
                {
                    if (existing.Exists)
                    {
                        MoveToHistory(target, history, relative);
                        removed++;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(file.FullName, copy, overwrite: true);
                    File.SetLastWriteTimeUtc(copy, file.LastWriteTimeUtc);
                    copied++;
                    bytes += file.Length;
                }

                manifest.Add(new BackupFileVersion(relative, file.Length, file.LastWriteTimeUtc));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failed++;
                if (firstError.Length == 0)
                {
                    firstError = $"{relative}: {exception.Message}";
                }
            }
        }

        // Files the folder no longer has: kept, dated, in the history.
        foreach (FileInfo gone in EnumerateFiles(new DirectoryInfo(target), skip: FolderBackupHistory.HistoryFolderName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(target, gone.FullName);
            if (seen.Contains(relative))
            {
                continue;
            }

            try
            {
                MoveToHistory(target, history, relative);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ApplicationLogService.WriteException("Folder backup: moving a removed file to the history", exception);
            }
        }

        RemoveEmptyFolders(new DirectoryInfo(target), skip: FolderBackupHistory.HistoryFolderName);
        FolderBackupHistory.WriteManifest(target, nowUtc, manifest);
        FolderBackupHistory.Prune(target, historyDays, nowUtc);
        return new CopyOutcome(manifest.Count + failed, copied, unchanged, failed, removed, bytes, firstError);
    }

    private static void MoveToHistory(string target, string historyRun, string relative)
    {
        string kept = Path.Combine(historyRun, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.Move(Path.Combine(target, relative), kept, overwrite: true);
    }

    private static IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo folder, string? skip = null)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = folder.EnumerateFileSystemInfos();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (FileSystemInfo entry in entries)
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue; // junctions and links can loop, and point outside the folder
            }

            if (entry is DirectoryInfo directory)
            {
                if (skip is not null && directory.Name.Equals(skip, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (FileInfo nested in EnumerateFiles(directory))
                {
                    yield return nested;
                }
            }
            else if (entry is FileInfo file)
            {
                yield return file;
            }
        }
    }

    private static void RemoveEmptyFolders(DirectoryInfo folder, string? skip)
    {
        foreach (DirectoryInfo child in folder.EnumerateDirectories().Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint)
                     && (skip is null || !child.Name.Equals(skip, StringComparison.OrdinalIgnoreCase))))
        {
            RemoveEmptyFolders(child, null);
            try
            {
                if (!child.EnumerateFileSystemInfos().Any())
                {
                    child.Delete();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.0} GB",
        >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B"
    };

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
