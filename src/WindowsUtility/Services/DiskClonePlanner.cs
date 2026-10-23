using System.Text;

namespace WindowsUtility.Services;

/// <summary>A partition as the cloner sees it (from Get-Partition and Get-Volume).</summary>
public sealed record ClonePartition(
    int Number,
    ulong Offset,
    ulong Size,
    string GptType,
    int MbrType,
    bool IsActive,
    bool IsBoot,
    bool IsSystem,
    char? Letter,
    string VolumePath,
    string FileSystem,
    ulong UsedBytes,
    string Label,
    bool IsEncrypted,
    bool IsLocked,
    string Guid)
{
    /// <summary>
    /// An unlocked BitLocker volume reads as plain data through its volume path (and a snapshot of it), so it is copied
    /// like any other and the copy is not encrypted. A locked one, or one Windows gives no volume, must be unlocked or
    /// decrypted first.
    /// </summary>
    public bool NeedsDecrypting => IsEncrypted && (IsLocked || VolumePath.Length == 0);

    public bool IsEfi => GptType.Equals(DiskClonePlanner.EfiType, StringComparison.OrdinalIgnoreCase);
    public bool IsMsr => GptType.Equals(DiskClonePlanner.MsrType, StringComparison.OrdinalIgnoreCase);
    public bool IsRecovery => GptType.Equals(DiskClonePlanner.RecoveryType, StringComparison.OrdinalIgnoreCase) || MbrType == 0x27;
    public bool IsNtfs => FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    /// <summary>"C: Windows", "EFI system", "Recovery", "D: Data".</summary>
    public string Title => IsEfi ? "EFI system" : IsMsr ? "Microsoft reserved"
        : IsRecovery ? (Label.Length > 0 && !Label.Equals("Recovery", StringComparison.OrdinalIgnoreCase) ? $"Recovery ({Label})" : "Recovery")
        : Letter is char letter ? $"{letter}:{(Label.Length > 0 ? " " + Label : string.Empty)}"
        : Label.Length > 0 ? Label : $"Partition {Number}";
}

public sealed record CloneDisk(int Number, string Name, ulong Size, string Style, int LogicalSectorSize, bool IsBoot, bool IsSystem,
    string BusType, IReadOnlyList<ClonePartition> Partitions)
{
    public string DisplayText => $"Disk {Number}: {Name}  ({DiskClonePlanner.FormatSize(Size)}, {Style}{(IsBoot ? ", Windows runs from it" : string.Empty)})";
    public bool HasWindows => Partitions.Any(partition => partition.IsBoot);
}

public enum CopyMode
{
    /// <summary>NTFS in use: Windows snapshot, only the clusters in use.</summary>
    SnapshotUsed,

    /// <summary>NTFS in use: Windows snapshot, every sector.</summary>
    SnapshotFull,

    /// <summary>Not NTFS (EFI, FAT) or no volume: every sector, read directly.</summary>
    Raw,

    /// <summary>Microsoft reserved partition: holds nothing, left empty.</summary>
    Skip
}

public sealed record PlannedPartition(ClonePartition Source, ulong Offset, ulong Size, CopyMode Mode)
{
    /// <summary>The partition gets more space than it had (extended after the copy).</summary>
    public bool Grows => Size > Source.Size;

    public bool Shrinks => Size < Source.Size;

    /// <summary>Bytes this partition copies (used space for a used-only copy).</summary>
    public ulong BytesToCopy => Mode switch
    {
        CopyMode.Skip => 0,
        CopyMode.SnapshotUsed => Math.Min(Source.Size, Source.UsedBytes + 64UL * DiskClonePlanner.MiB),
        _ => Source.Size
    };
}

public sealed record ClonePlan(
    IReadOnlyList<PlannedPartition> Partitions,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes,
    PlannedPartition? ShrinkFirst)
{
    public bool CanStart => Problems.Count == 0 && Partitions.Count > 0;
    public ulong TotalBytes => Partitions.Aggregate(0UL, (sum, partition) => sum + partition.BytesToCopy);
}

/// <summary>What a backup copies into its image file, and whether it can start.</summary>
public sealed record BackupPlan(
    IReadOnlyList<PlannedPartition> Partitions,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes,
    ulong BytesToCopy,
    ulong ImageSize)
{
    public bool CanStart => Problems.Count == 0 && Partitions.Any(partition => partition.Mode != CopyMode.Skip);
}

/// <summary>Where the boot files go to make a disk start the Windows on one of its partitions.</summary>
public sealed record BootSetupPlan(ClonePartition? Windows, ClonePartition? Boot, bool SetActive, IReadOnlyList<string> Problems)
{
    public bool CanStart => Problems.Count == 0 && Windows is not null && Boot is not null;
}

/// <summary>A free stretch of a disk, or an existing partition that may be replaced.</summary>
public sealed record CloneTargetSpace(ulong Offset, ulong Size, ClonePartition? Replaces)
{
    public string DisplayText => Replaces is null
        ? $"Free space: {DiskClonePlanner.FormatSize(Size)} (starting at {DiskClonePlanner.FormatSize(Offset)})"
        : $"Replace {Replaces.Title} ({DiskClonePlanner.FormatSize(Size)}): its data is erased";
}

/// <summary>
/// Plans a disk clone or a single partition copy: which partition goes where on the new disk and how it is copied.
/// Pure logic (no disk access), so it is tested on its own. Partitions keep their order and size; on a larger disk
/// the Windows partition (or the largest data partition) takes the extra space and the partitions after it, such as
/// recovery, move to the end. On a smaller disk the Windows partition must be shrunk first, if its data fits.
/// </summary>
public static class DiskClonePlanner
{
    public const ulong MiB = 1024UL * 1024;
    public const ulong GiB = 1024UL * MiB;
    public const string EfiType = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";
    public const string MsrType = "{e3c9e316-0b5c-4db8-817d-f92df00215ae}";
    public const string RecoveryType = "{de94bba4-06d1-4d40-a16a-bfd50179d6ac}";
    public const string BasicDataType = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";

    /// <summary>Dynamic disk (LDM) metadata and data partitions.</summary>
    private static readonly string[] DynamicTypes = { "{5808c8aa-7e8f-42e0-85d2-e1e90434cfb3}", "{af9b60a0-1431-4f62-bc68-3311714a69ad}" };

    /// <summary>Room left at the end of a GPT disk for the backup partition table.</summary>
    private const ulong GptTail = MiB;

    public static ClonePlan PlanClone(CloneDisk source, CloneDisk target)
    {
        List<string> problems = new();
        List<string> notes = new();
        if (source.Number == target.Number)
        {
            problems.Add("Choose two different disks.");
        }

        if (target.IsBoot || target.IsSystem)
        {
            problems.Add($"Disk {target.Number} is the disk Windows is running from. It can only be copied from, never to.");
        }

        if (source.Style is not ("GPT" or "MBR"))
        {
            problems.Add($"Disk {source.Number} has no partition table ({source.Style}), so there is nothing to clone.");
        }

        if (source.LogicalSectorSize != target.LogicalSectorSize)
        {
            problems.Add($"The disks use different sector sizes ({source.LogicalSectorSize} and {target.LogicalSectorSize} bytes), so a sector copy would not be readable.");
        }

        foreach (ClonePartition encrypted in source.Partitions.Where(partition => partition.IsEncrypted))
        {
            if (encrypted.NeedsDecrypting)
            {
                problems.Add(EncryptedProblem(encrypted));
            }
            else
            {
                notes.Add(EncryptedNote(encrypted));
            }
        }

        if (source.Partitions.Any(partition => DynamicTypes.Contains(partition.GptType, StringComparer.OrdinalIgnoreCase) || partition.MbrType == 0x42))
        {
            problems.Add($"Disk {source.Number} is a dynamic disk, which this cloner does not copy. Convert it to a basic disk first.");
        }

        if (source.Style == "MBR" && source.Partitions.Any(partition => partition.MbrType is 0x05 or 0x0F))
        {
            problems.Add("The disk has extended (logical) partitions, which this cloner does not copy.");
        }

        List<ClonePartition> partitions = source.Partitions.OrderBy(partition => partition.Offset).ToList();
        if (partitions.Count == 0)
        {
            problems.Add($"Disk {source.Number} has no partitions to copy.");
            return new ClonePlan(Array.Empty<PlannedPartition>(), problems, notes, null);
        }

        ulong end = target.Style == "MBR" || source.Style == "MBR" ? AlignDown(target.Size) : AlignDown(target.Size - GptTail);
        ClonePartition grow = partitions.FirstOrDefault(partition => partition.IsBoot)
            ?? partitions.Where(partition => !partition.IsEfi && !partition.IsMsr && !partition.IsRecovery).OrderByDescending(partition => partition.Size).FirstOrDefault()
            ?? partitions[^1];
        int growIndex = partitions.IndexOf(grow);

        // Partitions before the growing one keep their place; the ones after it are packed against the end.
        ulong afterSize = partitions.Skip(growIndex + 1).Aggregate(0UL, (sum, partition) => sum + AlignUp(partition.Size));
        ulong growOffset = grow.Offset;
        ulong growSpace = end > growOffset + afterSize ? end - growOffset - afterSize : 0;
        ulong growSize = AlignDown(growSpace);
        if (!grow.IsNtfs && growSize > grow.Size)
        {
            // Windows can only extend NTFS; any other file system keeps its size (the rest stays free).
            growSize = grow.Size;
        }
        else if (growSize > grow.Size && growSize - grow.Size < GiB)
        {
            // Same-size disks differ by a few megabytes; growing for that is a needless step.
            growSize = grow.Size;
        }

        PlannedPartition? shrinkFirst = null;
        if (growSize < grow.Size)
        {
            ulong minimum = MinimumSize(grow);
            if (growSize < minimum || !grow.IsNtfs)
            {
                problems.Add($"Disk {target.Number} ({FormatSize(target.Size)}) is too small: {grow.Title} needs at least {FormatSize(minimum)} and only {FormatSize(growSize)} is free there.");
                growSize = grow.Size;
            }
            else
            {
                shrinkFirst = new PlannedPartition(grow, grow.Offset, growSize, CopyMode.SnapshotUsed);
                notes.Add($"The new disk is smaller: {grow.Title} on Disk {source.Number} is shrunk first from {FormatSize(grow.Size)} to {FormatSize(growSize)} (Windows' own shrink, as in Disk Management; {FormatSize(grow.UsedBytes)} is in use).");
            }
        }
        else if (growSize > grow.Size)
        {
            notes.Add($"{grow.Title} grows from {FormatSize(grow.Size)} to {FormatSize(growSize)} to use the whole new disk.");
        }

        List<PlannedPartition> planned = new();
        foreach (ClonePartition partition in partitions.Take(growIndex))
        {
            planned.Add(new PlannedPartition(partition, partition.Offset, partition.Size, ModeFor(partition)));
        }

        planned.Add(new PlannedPartition(grow, growOffset, growSize, ModeFor(grow)));
        ulong next = end - afterSize;
        foreach (ClonePartition partition in partitions.Skip(growIndex + 1))
        {
            planned.Add(new PlannedPartition(partition, next, partition.Size, ModeFor(partition)));
            next += AlignUp(partition.Size);
        }

        if (planned.Any(partition => partition.Offset + partition.Size > end && partition.Source != grow))
        {
            problems.Add($"Disk {target.Number} is too small for the partitions of Disk {source.Number}.");
        }

        if (source.HasWindows)
        {
            notes.Add("Windows is copied from a snapshot, so programs can stay open; changes made after the copy starts are not copied.");
        }

        return new ClonePlan(planned, problems, notes, shrinkFirst);
    }

    /// <summary>Copies one partition into free space or over an existing partition.</summary>
    public static ClonePlan PlanPartitionCopy(ClonePartition source, int sourceSectorSize, CloneDisk target, CloneTargetSpace space, bool fillSpace)
    {
        List<string> problems = new();
        if (sourceSectorSize != target.LogicalSectorSize)
        {
            problems.Add($"The disks use different sector sizes ({sourceSectorSize} and {target.LogicalSectorSize} bytes).");
        }

        List<string> notes = new();
        if (source.NeedsDecrypting)
        {
            problems.Add(EncryptedProblem(source));
        }
        else if (source.IsEncrypted)
        {
            notes.Add(EncryptedNote(source));
        }

        if (space.Replaces is { } replaced && (replaced.IsBoot || replaced.IsSystem))
        {
            problems.Add($"{replaced.Title} is in use by Windows and cannot be replaced.");
        }

        if (space.Replaces is { } overwritten && overwritten.Guid.Length > 0 && overwritten.Guid == source.Guid)
        {
            problems.Add("A partition cannot be copied over itself.");
        }

        if (space.Size < source.Size)
        {
            problems.Add($"There is {FormatSize(space.Size)} there; {source.Title} needs {FormatSize(source.Size)}.");
        }

        ulong size = fillSpace ? AlignDown(space.Size) : source.Size;
        PlannedPartition planned = new(source, space.Offset, Math.Max(size, source.Size), ModeFor(source));
        return new ClonePlan(new[] { planned }, problems, notes, null);
    }

    /// <summary>
    /// A backup of a disk's chosen partitions into an image file: the partitions keep their place in an image as large
    /// as the disk (only the copied data takes room), so it can be restored like a clone or attached as a disk.
    /// </summary>
    public static BackupPlan PlanBackup(CloneDisk disk, IReadOnlyList<ClonePartition> chosen, ClonePartition? destinationOn, ulong? freeBytes)
    {
        List<string> problems = new();
        List<string> notes = new();
        if (disk.Style is not ("GPT" or "MBR"))
        {
            problems.Add($"Disk {disk.Number} has no partitions to back up.");
        }

        if (disk.Partitions.Any(partition => DynamicTypes.Contains(partition.GptType, StringComparer.OrdinalIgnoreCase) || partition.MbrType == 0x42))
        {
            problems.Add($"Disk {disk.Number} is a dynamic disk, which this backup does not copy.");
        }

        List<ClonePartition> partitions = chosen.Where(partition => !partition.IsMsr).OrderBy(partition => partition.Offset).ToList();
        if (partitions.Count == 0)
        {
            problems.Add("Tick at least one partition to back up.");
        }

        foreach (ClonePartition partition in partitions)
        {
            if (partition.NeedsDecrypting)
            {
                problems.Add(EncryptedProblem(partition));
            }
            else if (partition.IsEncrypted)
            {
                notes.Add($"{partition.Title} is encrypted with BitLocker and unlocked, so it is backed up readable; the backup file itself is not encrypted.");
            }
        }

        if (destinationOn is not null && partitions.Any(partition => partition.Guid.Length > 0 && partition.Guid == destinationOn.Guid))
        {
            problems.Add($"The backup cannot be saved on {destinationOn.Title}, a partition it backs up. Choose a folder on another partition, an external disk or a network share.");
        }

        // The MSR holds nothing but is recreated on a restore, so it is listed; the partition table is copied as well.
        List<PlannedPartition> planned = disk.Partitions.OrderBy(partition => partition.Offset)
            .Where(partition => partition.IsMsr || partitions.Contains(partition))
            .Select(partition => new PlannedPartition(partition, partition.Offset, partition.Size, ModeFor(partition))).ToList();
        ulong bytes = planned.Aggregate(2 * MiB, (sum, partition) => sum + partition.BytesToCopy);
        if (SpaceProblem(bytes, freeBytes, incremental: false) is string space)
        {
            problems.Add(space);
        }

        if (planned.Any(partition => partition.Mode == CopyMode.SnapshotUsed))
        {
            notes.Add("Partitions in use are backed up from a snapshot, so programs can stay open; changes made after the backup starts are not in it.");
        }

        ClonePartition? windows = disk.Partitions.FirstOrDefault(partition => partition.IsBoot);
        ClonePartition? boot = disk.Style == "GPT" ? disk.Partitions.FirstOrDefault(partition => partition.IsEfi) : disk.Partitions.FirstOrDefault(partition => partition.IsActive);
        if (windows is not null && (!partitions.Contains(windows) || (boot is not null && !partitions.Contains(boot))))
        {
            notes.Add($"Without {(partitions.Contains(windows) ? boot!.Title : windows.Title)} a disk restored from this backup will not start Windows; files can still be copied out of it.");
        }

        return new BackupPlan(planned, problems, notes, bytes, disk.Size);
    }

    /// <summary>
    /// Whether there is room where the image goes; null when there is. A full image needs about everything it copies.
    /// An incremental one holds only what changed since the last image, which is not known until it is compared, so it
    /// asks for a hundredth of the data (at least 1 GB) and stops with Windows' error if the changes do not fit.
    /// </summary>
    public static string? SpaceProblem(ulong bytes, ulong? freeBytes, bool incremental)
    {
        if (freeBytes is not ulong free)
        {
            return null;
        }

        if (incremental)
        {
            ulong least = Math.Max(GiB, bytes / 100);
            return free < least ? $"There is {FormatSize(free)} free where the backup would go; the changes since the last backup need room there (at least {FormatSize(least)})." : null;
        }

        return free < bytes + bytes / 20 ? $"There is {FormatSize(free)} free where the backup would go; about {FormatSize(bytes)} is needed." : null;
    }

    /// <summary>A locked encrypted partition (or one without a volume) would be copied as unreadable data.</summary>
    public static string EncryptedProblem(ClonePartition partition) =>
        $"{partition.Title} is encrypted with BitLocker and "
        + (partition.IsLocked
            ? "locked. Unlock it (open it in Explorer and enter its password or recovery key), or select Decrypt to turn BitLocker off on it, then copy."
            : "Windows gives it no volume to read it through"
              + (partition.Letter is null ? " (it has no drive letter, so Control Panel > BitLocker does not list it)" : string.Empty)
              + ". Select Decrypt to turn BitLocker off on it, then copy; BitLocker can be turned on again afterwards.");

    /// <summary>An unlocked encrypted partition is copied as plain data; BitLocker stays on where it is.</summary>
    public static string EncryptedNote(ClonePartition partition) =>
        $"{partition.Title} is encrypted with BitLocker and unlocked, so it is copied readable: BitLocker stays on here, "
        + "and the copy on the new disk is not encrypted (turn BitLocker on there afterwards if wanted).";

    /// <summary>
    /// After copying partitions one by one: the boot files go to the disk's EFI partition (GPT), or to its active
    /// partition (MBR; the Windows partition is made active when none is).
    /// </summary>
    public static BootSetupPlan PlanBootSetup(CloneDisk disk, ClonePartition? windows)
    {
        List<string> problems = new();
        if (disk.IsBoot || disk.IsSystem)
        {
            problems.Add($"Disk {disk.Number} is the disk Windows is running from; it already starts Windows.");
        }

        if (windows is null)
        {
            problems.Add($"Choose the partition on Disk {disk.Number} that holds the copied Windows (usually the largest NTFS partition).");
            return new BootSetupPlan(null, null, false, problems);
        }

        if (!windows.IsNtfs)
        {
            problems.Add($"{windows.Title} is not NTFS, so it cannot hold Windows.");
        }

        if (windows.NeedsDecrypting)
        {
            problems.Add($"{windows.Title} is encrypted with BitLocker and locked. Unlock it (enter its password or recovery key in Explorer) or decrypt it first.");
        }

        ClonePartition? boot;
        bool setActive = false;
        if (disk.Style == "GPT")
        {
            boot = disk.Partitions.FirstOrDefault(partition => partition.IsEfi);
            if (boot is null)
            {
                problems.Add($"Disk {disk.Number} has no EFI system partition. Copy the EFI system partition from the old disk to it first (Copy one partition).");
            }
        }
        else if (disk.Style == "MBR")
        {
            boot = disk.Partitions.FirstOrDefault(partition => partition.IsActive);
            if (boot is null)
            {
                boot = windows;
                setActive = true;
            }
        }
        else
        {
            boot = null;
            problems.Add($"Disk {disk.Number} has no partition table.");
        }

        return new BootSetupPlan(windows, boot, setActive, problems);
    }

    /// <summary>Free stretches of at least 16 MB, then existing partitions that could be replaced (not Windows' own).</summary>
    public static IReadOnlyList<CloneTargetSpace> TargetSpaces(CloneDisk disk)
    {
        List<CloneTargetSpace> spaces = new();
        ulong start = MiB;
        ulong end = disk.Style == "MBR" ? AlignDown(disk.Size) : AlignDown(disk.Size - GptTail);
        foreach (ClonePartition partition in disk.Partitions.OrderBy(partition => partition.Offset))
        {
            if (partition.Offset > start && partition.Offset - start >= 16 * MiB)
            {
                spaces.Add(new CloneTargetSpace(AlignUp(start), AlignDown(partition.Offset - AlignUp(start)), null));
            }

            start = Math.Max(start, partition.Offset + partition.Size);
        }

        if (end > AlignUp(start) && end - AlignUp(start) >= 16 * MiB)
        {
            spaces.Add(new CloneTargetSpace(AlignUp(start), end - AlignUp(start), null));
        }

        spaces.AddRange(disk.Partitions.Where(partition => !partition.IsBoot && !partition.IsSystem && !partition.IsEfi && !partition.IsMsr)
            .OrderBy(partition => partition.Offset)
            .Select(partition => new CloneTargetSpace(partition.Offset, partition.Size, partition)));
        return spaces;
    }

    /// <summary>The smallest a Windows or data partition may be shrunk to: what is used plus room to work (10%, at least 2 GB).</summary>
    public static ulong MinimumSize(ClonePartition partition) =>
        AlignUp(partition.UsedBytes + Math.Max(2 * GiB, partition.UsedBytes / 10));

    public static CopyMode ModeFor(ClonePartition partition) =>
        partition.IsMsr ? CopyMode.Skip
        : partition.IsNtfs && partition.VolumePath.Length > 0 ? CopyMode.SnapshotUsed
        : CopyMode.Raw;

    /// <summary>
    /// Byte ranges to copy for a used-only copy: every run of clusters in use (from the NTFS volume bitmap, one bit
    /// per cluster) plus the last megabyte of the partition, where NTFS keeps its backup boot sector. Neighbouring runs
    /// are merged so the copy reads in large pieces.
    /// </summary>
    public static List<(long Offset, long Length)> UsedRanges(byte[] bitmap, long clusterCount, int bytesPerCluster, long partitionSize, long mergeGap = 1024 * 1024)
    {
        List<(long Offset, long Length)> runs = new();
        long run = -1;
        for (long cluster = 0; cluster <= clusterCount; cluster++)
        {
            bool used = cluster < clusterCount && (bitmap[cluster >> 3] & (1 << (int)(cluster & 7))) != 0;
            if (used && run < 0)
            {
                run = cluster;
            }
            else if (!used && run >= 0)
            {
                runs.Add((run * bytesPerCluster, (cluster - run) * bytesPerCluster));
                run = -1;
            }
        }

        long tail = Math.Min(partitionSize, (long)MiB);
        runs.Add((partitionSize - tail, tail));

        // Sorted, then merged: the backup-boot-sector stretch can overlap the last runs.
        List<(long Offset, long Length)> ranges = new();
        foreach ((long offset, long length) in runs.OrderBy(range => range.Offset))
        {
            if (ranges.Count > 0 && offset <= ranges[^1].Offset + ranges[^1].Length + mergeGap)
            {
                (long lastOffset, long lastLength) = ranges[^1];
                ranges[^1] = (lastOffset, Math.Max(lastOffset + lastLength, offset + length) - lastOffset);
            }
            else
            {
                ranges.Add((offset, length));
            }
        }

        return ranges;
    }

    /// <summary>The ranges with the excluded stretches cut out of them (both lists are in any order; the result is sorted).</summary>
    public static List<(long Offset, long Length)> Subtract(IReadOnlyList<(long Offset, long Length)> ranges, IReadOnlyList<(long Offset, long Length)> excluded)
    {
        List<(long Offset, long Length)> cuts = excluded.Where(cut => cut.Length > 0).OrderBy(cut => cut.Offset).ToList();
        List<(long Offset, long Length)> result = new();
        int next = 0;
        foreach ((long offset, long length) in ranges.Where(range => range.Length > 0).OrderBy(range => range.Offset))
        {
            long start = offset;
            long end = offset + length;
            while (next < cuts.Count && cuts[next].Offset + cuts[next].Length <= start)
            {
                next++; // Cuts entirely before this range are done with (the ranges are sorted too).
            }

            for (int index = next; index < cuts.Count && cuts[index].Offset < end && start < end; index++)
            {
                (long cutOffset, long cutLength) = cuts[index];
                if (cutOffset > start)
                {
                    result.Add((start, cutOffset - start));
                }

                start = Math.Max(start, cutOffset + cutLength);
            }

            if (start < end)
            {
                result.Add((start, end - start));
            }
        }

        return result;
    }

    /// <summary>
    /// The MountedDevices value that gives a volume its drive letter, so the cloned Windows starts as C:. GPT:
    /// "DMIO:ID:" and the partition's GUID. MBR: the disk signature and the partition's byte offset.
    /// </summary>
    public static byte[] MountedDevicesValue(string style, Guid partitionGuid, uint diskSignature, ulong partitionOffset)
    {
        if (style == "GPT")
        {
            return Encoding.ASCII.GetBytes("DMIO:ID:").Concat(partitionGuid.ToByteArray()).ToArray();
        }

        return BitConverter.GetBytes(diskSignature).Concat(BitConverter.GetBytes(partitionOffset)).ToArray();
    }

    public static ulong AlignUp(ulong value) => (value + MiB - 1) / MiB * MiB;

    public static ulong AlignDown(ulong value) => value / MiB * MiB;

    public static string FormatSize(ulong bytes) => bytes >= 1024UL * GiB
        ? $"{bytes / (double)(1024UL * GiB):0.##} TB"
        : bytes >= GiB ? $"{bytes / (double)GiB:0.#} GB" : $"{bytes / (double)MiB:0} MB";
}
