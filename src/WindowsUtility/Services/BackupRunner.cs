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
            BackupPlan plan = DiskClonePlanner.PlanBackup(disk, disk.Partitions.Where(partition => !partition.IsMsr).ToList(), destinationOn, DiskCloneService.FreeSpace(schedule.Destination));
            if (!plan.CanStart)
            {
                return new BackupRunResult(false, string.Join(" ", plan.Problems));
            }

            BackupImage image = await _disks.BackupAsync(disk, plan, schedule.Destination, verify: true, progress, cancellationToken);
            int removed = RemoveOldImages(schedule.Destination, image.ImagePath, Math.Max(1, schedule.Keep));
            long size = new FileInfo(image.ImagePath).Length;
            return new BackupRunResult(true, $"Backup saved: {image.ImageFileName} ({DiskClonePlanner.FormatSize((ulong)size)})" + (removed > 0 ? $"; {removed} older backup{(removed == 1 ? "" : "s")} removed." : "."));
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

    /// <summary>Deletes this PC's older images in the folder beyond the number kept (newest first by name, which carries the date).</summary>
    internal static int RemoveOldImages(string folder, string newestImagePath, int keep)
    {
        List<string> images = Directory.GetFiles(folder, $"{Environment.MachineName}-*{BackupImage.ImageExtension}")
            .Where(path => File.Exists(BackupImage.DescriptionPathFor(path)))
            .OrderByDescending(path => string.Equals(path, newestImagePath, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int removed = 0;
        foreach (string old in images.Skip(keep))
        {
            try
            {
                File.Delete(old);
                File.Delete(BackupImage.DescriptionPathFor(old));
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

        return schedule.Keep is < 1 or > 30 ? "Keep between 1 and 30 backups." : null;
    }
}
