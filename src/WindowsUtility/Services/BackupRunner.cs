using System.IO;
using WindowsUtility.Models;

namespace WindowsUtility.Services;

/// <summary>
/// Runs a system image backup through the clone code. Needs administrator rights (it reads the whole disk), so the
/// background service runs it: on schedule, for "Run now" and for "Back up now".
/// </summary>
public sealed class BackupRunner
{
    private readonly DiskCloneService _disks = new();

    public async Task<BackupRunResult> RunImageAsync(BackupSchedule schedule, string password, IProgress<CloneProgress> progress, CancellationToken cancellationToken)
    {
        try
        {
            if (schedule.Destination.Length == 0)
            {
                return new BackupRunResult(false, "No destination folder is set.");
            }

            if (schedule.Destination.StartsWith(@"\\", StringComparison.Ordinal) && schedule.ShareUser.Length > 0)
            {
                await FolderBackupRunner.ConnectShareAsync(schedule.Destination, schedule.ShareUser, password, cancellationToken);
            }

            IReadOnlyList<CloneDisk> disks = await _disks.GetDisksAsync(cancellationToken);
            CloneDisk? disk = schedule.WindowsDisk
                ? disks.FirstOrDefault(each => each.IsBoot)
                : disks.FirstOrDefault(each => each.Name == schedule.DiskName && each.Size == schedule.DiskSize) ?? disks.FirstOrDefault(each => each.Name == schedule.DiskName);
            if (disk is null)
            {
                return new BackupRunResult(false, schedule.WindowsDisk ? "The disk Windows runs from was not found." : $"The disk \"{schedule.DiskName}\" is not connected.");
            }

            string? volume = DiskCloneService.VolumePathOf(schedule.Destination);
            ClonePartition? destinationOn = volume is null ? null : disks.SelectMany(each => each.Partitions).FirstOrDefault(partition => partition.VolumePath.Equals(volume, StringComparison.OrdinalIgnoreCase));
            // Room is checked after the kind of backup is known: an incremental image needs far less than a full one.
            ulong? free = DiskCloneService.FreeSpace(schedule.Destination);
            BackupPlan plan = DiskClonePlanner.PlanBackup(disk, disk.Partitions.Where(partition => !partition.IsMsr).ToList(), destinationOn, freeBytes: null);
            if (!plan.CanStart)
            {
                return new BackupRunResult(false, string.Join(" ", plan.Problems));
            }

            BackupImage? parent = schedule.Incremental ? ChooseParent(ImagesIn(schedule.Destination), plan, Math.Max(2, schedule.FullEvery)) : null;
            if (DiskClonePlanner.SpaceProblem(plan.BytesToCopy, free, incremental: parent is not null) is string space)
            {
                return new BackupRunResult(false, space);
            }

            BackupImage image;
            try
            {
                image = await _disks.BackupAsync(disk, plan, schedule.Destination, verify: true, progress, cancellationToken, parent);
            }
            catch (IncrementalNotPossibleException exception) when (parent is not null)
            {
                if (DiskClonePlanner.SpaceProblem(plan.BytesToCopy, free, incremental: false) is string fullSpace)
                {
                    return new BackupRunResult(false, $"A full backup is needed because {exception.Message} {fullSpace}");
                }

                progress.Report(new CloneProgress(string.Empty, -1, -1, 0, $"A full backup is made instead: {exception.Message}"));
                image = await _disks.BackupAsync(disk, plan, schedule.Destination, verify: true, progress, cancellationToken);
            }

            int removed = RemoveOldImages(schedule.Destination, image.ImagePath, Math.Max(1, schedule.Keep));
            long size = new FileInfo(image.ImagePath).Length;
            string what = image.IsIncremental ? $"Changes since the last backup saved: {image.ImageFileName}" : $"Full backup saved: {image.ImageFileName}";
            return new BackupRunResult(true, $"{what} ({DiskClonePlanner.FormatSize((ulong)size)})" + (removed > 0 ? $"; {removed} older backup{(removed == 1 ? "" : "s")} removed." : "."));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Management.ManagementException or System.Text.Json.JsonException)
        {
            ApplicationLogService.WriteException($"Image backup: {schedule.Name}", exception);
            return new BackupRunResult(false, exception.Message);
        }
    }

    /// <summary>This PC's images in a folder (those with a description), newest first by name, which carries the date.</summary>
    public static List<BackupImage> ImagesIn(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return new List<BackupImage>();
        }

        return Directory.GetFiles(folder, $"{Environment.MachineName}-*{BackupImage.ImageExtension}")
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => { try { return BackupImage.Load(path); } catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException) { return null; } })
            .Where(image => image is not null)
            .Select(image => image!)
            .ToList();
    }

    /// <summary>
    /// The image an incremental backup builds on: the newest one, when its chain is shorter than the full-image
    /// interval, is complete, and holds the same partitions as the disk has now. Null means a full backup.
    /// </summary>
    public static BackupImage? ChooseParent(IReadOnlyList<BackupImage> images, BackupPlan plan, int fullEvery)
    {
        BackupImage? newest = images.FirstOrDefault();
        if (newest is null || !newest.ChainComplete() || newest.Chain().Count >= fullEvery)
        {
            return null;
        }

        // An image made before 2.0.91 has Windows' 32 MB blocks: an incremental image built on it would grow by 32 MB
        // for every changed 4 KB. A new full image with small blocks is made instead.
        if (Vhdx.BlockSize(newest.ImagePath) is uint blockSize && blockSize > Vhdx.LargestParentBlockSize)
        {
            return null;
        }

        List<ClonePartition> stored = newest.Partitions.Select(partition => partition.Partition).OrderBy(partition => partition.Offset).ToList();
        List<PlannedPartition> planned = plan.Partitions.OrderBy(partition => partition.Offset).ToList();
        bool same = stored.Count == planned.Count && stored.Zip(planned).All(pair =>
            pair.First.Offset == pair.Second.Offset && pair.First.Size == pair.Second.Source.Size
            && pair.First.GptType.Equals(pair.Second.Source.GptType, StringComparison.OrdinalIgnoreCase) && pair.First.MbrType == pair.Second.Source.MbrType);
        return same ? newest : null;
    }

    /// <summary>
    /// Deletes this PC's older backups in the folder beyond the number of full images kept. Each full image goes with
    /// the incremental images built on it, so a chain is only ever removed whole.
    /// </summary>
    internal static int RemoveOldImages(string folder, string newestImagePath, int keep)
    {
        List<BackupImage> images = ImagesIn(folder);
        List<string> chains = images.OrderByDescending(image => string.Equals(image.ImagePath, newestImagePath, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(image => image.ChainBase, StringComparer.OrdinalIgnoreCase)
            .Select(image => image.ChainBase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        HashSet<string> kept = chains.Take(keep).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int removed = 0;
        foreach (BackupImage old in images.Where(image => !kept.Contains(image.ChainBase)))
        {
            try
            {
                File.Delete(old.ImagePath);
                File.Delete(BackupImage.DescriptionPathFor(old.ImagePath));
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ApplicationLogService.WriteException("Image backup: removing an old image", exception);
            }
        }

        return removed;
    }

    /// <summary>What is wrong with an image backup schedule before it is saved; null when it is fine.</summary>
    public static string? Validate(BackupSchedule schedule)
    {
        if (schedule.Name.Trim().Length == 0)
        {
            return "Give the backup a name.";
        }

        if (schedule.Destination.Trim().Length == 0)
        {
            return "Choose where to save the backup.";
        }

        if (schedule.Frequency == BackupFrequency.Live)
        {
            return "A system image cannot be live; choose how often it runs.";
        }

        if (schedule.Frequency == BackupFrequency.Interval && schedule.IntervalMinutes < 60)
        {
            return "A system image copies the whole disk, so it can run at most once an hour.";
        }

        if (schedule.Frequency == BackupFrequency.Weekly && schedule.WeekDays.Count == 0)
        {
            return "Tick at least one day of the week.";
        }

        if (schedule.Incremental && schedule.FullEvery is < 2 or > 60)
        {
            return "A new full image every 2 to 60 runs.";
        }

        return schedule.Keep is < 1 or > 30 ? "Keep between 1 and 30 backups." : null;
    }
}
