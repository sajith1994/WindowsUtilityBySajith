using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using WindowsUtility.Models;

namespace WindowsUtility.Services;

public sealed record BackupRunResult(bool Succeeded, string Message);

/// <summary>
/// Runs one automatic backup: a system image through the clone code, or a folder copy with robocopy (newer files
/// only, nothing deleted at the destination). Used by the background service on schedule and by "Run now".
/// </summary>
public sealed class BackupRunner
{
    private readonly DiskCloneService _disks = new();

    public async Task<BackupRunResult> RunAsync(BackupSchedule schedule, IProgress<CloneProgress> progress, CancellationToken cancellationToken)
    {
        try
        {
            if (schedule.Destination.Length == 0)
            {
                return new BackupRunResult(false, "No destination folder is set.");
            }

            if (schedule.Destination.StartsWith(@"\\", StringComparison.Ordinal) && schedule.ShareUser.Length > 0)
            {
                await DiskCloneService.ConnectShareAsync(schedule.Destination, schedule.ShareUser, BackupScheduleStore.Unprotect(schedule.ShareSecret), cancellationToken);
            }

            return schedule.Kind == BackupKind.Folder
                ? await CopyFolderAsync(schedule, progress, cancellationToken)
                : await ImageAsync(schedule, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or System.Management.ManagementException or System.Text.Json.JsonException)
        {
            ApplicationLogService.WriteException($"Automatic backup: {schedule.Name}", exception);
            return new BackupRunResult(false, exception.Message);
        }
    }

    private async Task<BackupRunResult> ImageAsync(BackupSchedule schedule, IProgress<CloneProgress> progress, CancellationToken cancellationToken)
    {
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
                ApplicationLogService.WriteException("Automatic backup: removing an old image", exception);
            }
        }

        return removed;
    }

    private static async Task<BackupRunResult> CopyFolderAsync(BackupSchedule schedule, IProgress<CloneProgress> progress, CancellationToken cancellationToken)
    {
        string source = schedule.SourceFolder.TrimEnd('\\');
        if (!Directory.Exists(source))
        {
            return new BackupRunResult(false, $"The folder {source} was not found.");
        }

        string leaf = Path.GetFileName(source);
        if (leaf.Length == 0)
        {
            leaf = source.Replace(":", string.Empty).Trim('\\'); // a whole drive, "D"
        }

        string target = Path.Combine(schedule.Destination, leaf);
        Directory.CreateDirectory(target);
        progress.Report(new CloneProgress($"Copying {source}...", 0, 1, 0));
        progress.Report(new CloneProgress(string.Empty, -1, -1, 0, $"Copying {source} to {target} (newer files only; nothing is deleted there)."));

        // /E all folders, /XO only files newer than the copy, /B backup rights for files the user locked down,
        // /R:1 one retry, /XJ no junctions (they loop), /NP /NFL /NDL summary only.
        ProcessStartInfo startInfo = new("robocopy.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { source, target, "/E", "/COPY:DAT", "/DCOPY:DAT", "/XO", "/B", "/R:1", "/W:2", "/XJ", "/NP", "/NFL", "/NDL", "/NJH" })
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
        progress.Report(new CloneProgress(string.Empty, -1, -1, 0, text));
        return new BackupRunResult(ok, text);
    }

    /// <summary>"Files: 1234 total, 56 copied, 2 failed, 1.2 GB" from robocopy's summary table.</summary>
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
}
