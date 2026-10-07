using System.IO;
using System.Text;

namespace CentricDeviceMonitor.Services;

public sealed record TemporaryFileCleanupItem(
    string ItemType,
    string Path,
    string Result,
    long Bytes,
    string Details)
{
    public string SizeDisplay => FormatBytes(Bytes);

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}

public sealed record TemporaryFileCleanupVolumeResult(
    string Volume,
    long FreeBefore,
    long FreeAfter)
{
    public long ObservedIncrease => Math.Max(0, FreeAfter - FreeBefore);
    public string FreeBeforeDisplay => FormatBytes(FreeBefore);
    public string FreeAfterDisplay => FormatBytes(FreeAfter);
    public string ObservedIncreaseDisplay => FormatBytes(ObservedIncrease);

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}

public sealed record TemporaryFileCleanupResult(
    int FilesDeleted,
    int DirectoriesDeleted,
    int ItemsSkipped,
    long BytesFreed,
    IReadOnlyList<TemporaryFileCleanupItem> Items,
    IReadOnlyList<TemporaryFileCleanupVolumeResult> Volumes,
    string LogFilePath)
{
    public long ObservedFreeSpaceIncrease => Volumes.Sum(volume => volume.ObservedIncrease);
    public string SizeDisplay => FormatBytes(BytesFreed);
    public string ObservedFreeSpaceIncreaseDisplay => FormatBytes(ObservedFreeSpaceIncrease);

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}

public sealed class TemporaryFileCleanupService
{
    public static string LogFilePath => Path.Combine(
        SharedDataPaths.LogsDirectory,
        "temporary-file-cleanup.csv");

    public Task<TemporaryFileCleanupResult> CleanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Clean(cancellationToken), cancellationToken);

    private static TemporaryFileCleanupResult Clean(CancellationToken cancellationToken)
    {
        CleanupAccumulator accumulator = new();
        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);

        AddRoot(roots, Path.GetTempPath());
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            AddRoot(roots, Path.Combine(windowsDirectory, "Temp"));
        }

        Dictionary<string, long> freeSpaceBefore = CaptureVolumeFreeSpace(roots);

        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanDirectoryContents(root, accumulator, cancellationToken);
        }

        Dictionary<string, long> freeSpaceAfter = CaptureVolumeFreeSpace(roots);
        List<TemporaryFileCleanupVolumeResult> volumes = freeSpaceBefore
            .Keys
            .Union(freeSpaceAfter.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(volume => new TemporaryFileCleanupVolumeResult(
                volume,
                freeSpaceBefore.GetValueOrDefault(volume),
                freeSpaceAfter.GetValueOrDefault(volume)))
            .ToList();

        TemporaryFileCleanupResult result = new(
            accumulator.FilesDeleted,
            accumulator.DirectoriesDeleted,
            accumulator.ItemsSkipped,
            accumulator.BytesFreed,
            accumulator.Items.ToArray(),
            volumes,
            LogFilePath);

        AppendLog(result);
        return result;
    }

    private static void AddRoot(ISet<string> roots, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            roots.Add(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));
        }
        catch
        {
        }
    }

    private static Dictionary<string, long> CaptureVolumeFreeSpace(IEnumerable<string> roots)
    {
        Dictionary<string, long> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            try
            {
                string? volumeRoot = Path.GetPathRoot(root);
                if (string.IsNullOrWhiteSpace(volumeRoot) || values.ContainsKey(volumeRoot))
                {
                    continue;
                }

                DriveInfo drive = new(volumeRoot);
                if (drive.IsReady)
                {
                    values[volumeRoot] = drive.AvailableFreeSpace;
                }
            }
            catch
            {
            }
        }

        return values;
    }

    private static void CleanDirectoryContents(
        string directory,
        CleanupAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        }
        catch (Exception exception)
        {
            accumulator.ItemsSkipped++;
            accumulator.Items.Add(new TemporaryFileCleanupItem(
                "Folder",
                directory,
                "Skipped",
                0,
                exception.Message));
            return;
        }

        foreach (string entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    accumulator.ItemsSkipped++;
                    accumulator.Items.Add(new TemporaryFileCleanupItem(
                        "Link",
                        entry,
                        "Skipped",
                        0,
                        "Reparse points are not deleted for safety."));
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    CleanDirectoryContents(entry, accumulator, cancellationToken);
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(entry).Any())
                        {
                            Directory.Delete(entry, false);
                            accumulator.DirectoriesDeleted++;
                            accumulator.Items.Add(new TemporaryFileCleanupItem(
                                "Folder",
                                entry,
                                "Deleted",
                                0,
                                string.Empty));
                        }
                    }
                    catch (Exception exception)
                    {
                        accumulator.ItemsSkipped++;
                        accumulator.Items.Add(new TemporaryFileCleanupItem(
                            "Folder",
                            entry,
                            "Skipped",
                            0,
                            exception.Message));
                    }

                    continue;
                }

                long length = 0;
                try
                {
                    length = new FileInfo(entry).Length;
                }
                catch
                {
                }

                try
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }
                catch
                {
                }

                File.Delete(entry);
                accumulator.FilesDeleted++;
                accumulator.BytesFreed += Math.Max(0, length);
                accumulator.Items.Add(new TemporaryFileCleanupItem(
                    "File",
                    entry,
                    "Deleted",
                    Math.Max(0, length),
                    string.Empty));
            }
            catch (Exception exception)
            {
                accumulator.ItemsSkipped++;
                accumulator.Items.Add(new TemporaryFileCleanupItem(
                    Directory.Exists(entry) ? "Folder" : "File",
                    entry,
                    "Skipped",
                    0,
                    exception.Message));
            }
        }
    }

    private static void AppendLog(TemporaryFileCleanupResult result)
    {
        try
        {
            SharedDataPaths.EnsureDirectories();
            bool writeHeader = !File.Exists(LogFilePath) || new FileInfo(LogFilePath).Length == 0;
            using StreamWriter writer = new(LogFilePath, append: true, Encoding.UTF8);
            if (writeHeader)
            {
                writer.WriteLine("RunAt,Result,ItemType,Bytes,Path,Details");
            }

            string runAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (TemporaryFileCleanupItem item in result.Items)
            {
                writer.WriteLine(string.Join(",",
                    Csv(runAt),
                    Csv(item.Result),
                    Csv(item.ItemType),
                    item.Bytes.ToString(),
                    Csv(item.Path),
                    Csv(item.Details)));
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Temporary cleanup log", exception);
        }
    }

    private static string Csv(string value) =>
        $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    private sealed class CleanupAccumulator
    {
        public int FilesDeleted { get; set; }
        public int DirectoriesDeleted { get; set; }
        public int ItemsSkipped { get; set; }
        public long BytesFreed { get; set; }
        public List<TemporaryFileCleanupItem> Items { get; } = new();
    }
}
