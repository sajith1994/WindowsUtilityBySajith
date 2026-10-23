using System.IO;
using System.Text.Json;

namespace WindowsUtility.Services;

/// <summary>One file as it was at a backup run.</summary>
public sealed record BackupFileVersion(string RelativePath, long Size, DateTime ModifiedUtc);

/// <summary>A backup run: when it was, and every file the folder held then.</summary>
public sealed record BackupVersion(DateTime TakenUtc, string ManifestPath)
{
    public string Title => TakenUtc.ToLocalTime().ToString("ddd dd MMM yyyy HH:mm");
}

/// <summary>
/// The layout of a folder backup: the copy mirrors the folder; files it replaced or removed go to a dated folder
/// under "_History" (so an accidental change can be undone); each run writes a list of every file as it was then.
/// Restoring "as of" a run puts those files back: unchanged ones from the copy, older ones from the history.
/// </summary>
public static class FolderBackupHistory
{
    public const string HistoryFolderName = "_History";
    private const string ManifestExtension = ".files.json";
    private const string StampFormat = "yyyy-MM-dd HHmm-ss";

    public static string HistoryFolder(string target) => Path.Combine(target, HistoryFolderName);

    public static string RunFolder(string target, DateTime takenUtc) => Path.Combine(HistoryFolder(target), takenUtc.ToLocalTime().ToString(StampFormat));

    public static string ManifestPath(string target, DateTime takenUtc) => RunFolder(target, takenUtc) + ManifestExtension;

    /// <summary>Two timestamps count as the same when within two seconds (FAT and network shares round them).</summary>
    public static bool SameFile(long size, DateTime modifiedUtc, FileInfo copy) =>
        copy.Length == size && Math.Abs((copy.LastWriteTimeUtc - modifiedUtc).TotalSeconds) <= 2;

    public static void WriteManifest(string target, DateTime takenUtc, IReadOnlyList<BackupFileVersion> files)
    {
        Directory.CreateDirectory(HistoryFolder(target));
        File.WriteAllText(ManifestPath(target, takenUtc), JsonSerializer.Serialize(files));
    }

    /// <summary>The runs a backup has, newest first.</summary>
    public static List<BackupVersion> Versions(string target)
    {
        string history = HistoryFolder(target);
        if (!Directory.Exists(history))
        {
            return new List<BackupVersion>();
        }

        return Directory.GetFiles(history, "*" + ManifestExtension)
            .Select(path => (Path: path, Taken: ParseStamp(Path.GetFileName(path)[..^ManifestExtension.Length])))
            .Where(item => item.Taken is not null)
            .Select(item => new BackupVersion(item.Taken!.Value, item.Path))
            .OrderByDescending(version => version.TakenUtc)
            .ToList();
    }

    public static List<BackupFileVersion> Files(BackupVersion version) =>
        JsonSerializer.Deserialize<List<BackupFileVersion>>(File.ReadAllText(version.ManifestPath)) ?? new List<BackupFileVersion>();

    /// <summary>
    /// Where a file's content is now: in the copy when unchanged since, otherwise in the history folder of the run
    /// that replaced or removed it (the earliest one after the version asked for). Null when it is gone (history
    /// pruned).
    /// </summary>
    public static string? Locate(string target, BackupFileVersion file, IReadOnlyList<BackupVersion> versions, BackupVersion asOf)
    {
        string current = Path.Combine(target, file.RelativePath);
        if (File.Exists(current) && SameFile(file.Size, file.ModifiedUtc, new FileInfo(current)))
        {
            return current;
        }

        foreach (BackupVersion later in versions.Where(version => version.TakenUtc > asOf.TakenUtc).OrderBy(version => version.TakenUtc))
        {
            string kept = Path.Combine(RunFolder(target, later.TakenUtc), file.RelativePath);
            if (File.Exists(kept) && SameFile(file.Size, file.ModifiedUtc, new FileInfo(kept)))
            {
                return kept;
            }
        }

        return null;
    }

    /// <summary>
    /// Copies the folder as it was at a run into a destination (the original folder, or another). Files already
    /// identical there are left; others are replaced. Returns what was restored and what could not be found.
    /// </summary>
    public static (int Restored, int Unchanged, List<string> Missing) Restore(string target, BackupVersion asOf, string destination,
        Action<string, int, int>? progress, CancellationToken cancellationToken)
    {
        List<BackupVersion> versions = Versions(target);
        List<BackupFileVersion> files = Files(asOf);
        int restored = 0, unchanged = 0;
        List<string> missing = new();
        for (int index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackupFileVersion file = files[index];
            string into = Path.Combine(destination, file.RelativePath);
            if (File.Exists(into) && SameFile(file.Size, file.ModifiedUtc, new FileInfo(into)))
            {
                unchanged++;
                continue;
            }

            string? from = Locate(target, file, versions, asOf);
            if (from is null)
            {
                missing.Add(file.RelativePath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(into)!);
            File.Copy(from, into, overwrite: true);
            File.SetLastWriteTimeUtc(into, file.ModifiedUtc);
            restored++;
            progress?.Invoke(file.RelativePath, index + 1, files.Count);
        }

        return (restored, unchanged, missing);
    }

    /// <summary>Removes history older than the days kept (0 keeps everything); the newest run's list always stays.</summary>
    public static int Prune(string target, int historyDays, DateTime nowUtc)
    {
        if (historyDays <= 0)
        {
            return 0;
        }

        DateTime cutoff = nowUtc.AddDays(-historyDays);
        List<BackupVersion> versions = Versions(target);
        int removed = 0;
        foreach (BackupVersion old in versions.Skip(1).Where(version => version.TakenUtc < cutoff))
        {
            try
            {
                string folder = RunFolder(target, old.TakenUtc);
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }

                File.Delete(old.ManifestPath);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ApplicationLogService.WriteException("Folder backup history", exception);
            }
        }

        return removed;
    }

    private static DateTime? ParseStamp(string name) =>
        DateTime.TryParseExact(name, StampFormat, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out DateTime local)
            ? local.ToUniversalTime()
            : null;
}
