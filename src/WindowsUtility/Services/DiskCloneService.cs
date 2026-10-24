using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace WindowsUtility.Services;

public sealed record CloneOptions(bool UsedSpaceOnly, bool MakeBootable, bool Verify);

/// <summary>An incremental backup cannot build on the image chosen; a full backup is the way out.</summary>
public sealed class IncrementalNotPossibleException : InvalidOperationException
{
    public IncrementalNotPossibleException(string message) : base(message)
    {
    }
}

public sealed record CloneProgress(string Stage, long Done, long Total, double BytesPerSecond, string? LogLine = null)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Done / (double)Total, 0, 1);

    public TimeSpan? Remaining => BytesPerSecond > 1 && Total > Done ? TimeSpan.FromSeconds((Total - Done) / BytesPerSecond) : null;
}

/// <summary>
/// Clones a disk to another (moving Windows to a new SSD) or copies one partition, sector by sector. Partitions in
/// use are read from a Volume Shadow Copy snapshot, so the copy is consistent while Windows runs; with "used space
/// only" just the clusters NTFS has in use are copied. The source disk is only read (apart from an optional shrink the
/// user agrees to). Needs administrator rights.
/// </summary>
public sealed class DiskCloneService
{
    private const int ChunkSize = 4 * 1024 * 1024;

    // ---- Reading the disks -----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CloneDisk>> GetDisksAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $encrypted = @{}
            $locked = @{}
            try {
                Get-CimInstance -Namespace root/CIMV2/Security/MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume -ErrorAction Stop |
                    ForEach-Object {
                        # 1-5 = encrypted, or being encrypted or decrypted. The property is empty on some volumes
                        # (recovery partitions), so ask the volume itself; an empty answer is not encryption.
                        $status = $_.ConversionStatus
                        if ($null -eq $status) {
                            try { $status = (Invoke-CimMethod -InputObject $_ -MethodName GetConversionStatus -ErrorAction Stop).ConversionStatus } catch { }
                        }
                        $encrypted[[string]$_.DeviceID] = ($null -ne $status -and [int]$status -ge 1 -and [int]$status -le 5)
                        # An unlocked volume reads as plain data through its volume path, so it can be copied as it is.
                        $lock = 0
                        try { $lock = (Invoke-CimMethod -InputObject $_ -MethodName GetLockStatus -ErrorAction Stop).LockStatus } catch { }
                        $locked[[string]$_.DeviceID] = ([int]$lock -eq 1)
                    }
            } catch { }
            $disks = foreach ($d in Get-Disk) {
                $parts = @()
                if ([string]$d.PartitionStyle -ne 'RAW') {
                    $parts = foreach ($p in (Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue | Sort-Object Offset)) {
                        $v = $null
                        try { $v = Get-Volume -Partition $p -ErrorAction Stop } catch { }
                        $path = ''
                        if ($v) { $path = [string]$v.Path }
                        [pscustomobject]@{
                            Number = [int]$p.PartitionNumber; Offset = [uint64]$p.Offset; Size = [uint64]$p.Size
                            GptType = [string]$p.GptType; MbrType = [int]$p.MbrType; IsActive = [bool]$p.IsActive
                            IsBoot = [bool]$p.IsBoot; IsSystem = [bool]$p.IsSystem; Letter = [string]$p.DriveLetter
                            VolumePath = $path; FileSystem = $(if ($v) { [string]$v.FileSystem } else { '' })
                            Used = $(if ($v -and $v.Size) { [uint64]($v.Size - $v.SizeRemaining) } else { [uint64]0 })
                            Label = $(if ($v) { [string]$v.FileSystemLabel } else { '' })
                            Encrypted = [bool]($path -and $encrypted.ContainsKey($path) -and $encrypted[$path])
                            Locked = [bool]($path -and $locked.ContainsKey($path) -and $locked[$path])
                            Guid = [string]$p.Guid
                        }
                    }
                }
                [pscustomobject]@{
                    Number = [int]$d.Number; Name = [string]$d.FriendlyName; Size = [uint64]$d.Size; Style = [string]$d.PartitionStyle
                    Sector = [int]$d.LogicalSectorSize; IsBoot = [bool]$d.IsBoot; IsSystem = [bool]$d.IsSystem; Bus = [string]$d.BusType
                    Partitions = @($parts)
                }
            }
            ConvertTo-Json -InputObject @($disks) -Depth 5 -Compress
            """;

        string json = await RunPowerShellAsync(script, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json.Trim());
        List<CloneDisk> disks = new();
        foreach (JsonElement disk in document.RootElement.EnumerateArray())
        {
            List<ClonePartition> partitions = new();
            if (disk.TryGetProperty("Partitions", out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement part in parts.EnumerateArray())
                {
                    string letter = Text(part, "Letter");
                    partitions.Add(new ClonePartition(
                        part.GetProperty("Number").GetInt32(),
                        part.GetProperty("Offset").GetUInt64(),
                        part.GetProperty("Size").GetUInt64(),
                        Text(part, "GptType").ToLowerInvariant(),
                        part.GetProperty("MbrType").GetInt32(),
                        part.GetProperty("IsActive").GetBoolean(),
                        part.GetProperty("IsBoot").GetBoolean(),
                        part.GetProperty("IsSystem").GetBoolean(),
                        letter.Length > 0 && char.IsLetter(letter[0]) ? char.ToUpperInvariant(letter[0]) : null,
                        Text(part, "VolumePath"),
                        Text(part, "FileSystem"),
                        part.GetProperty("Used").GetUInt64(),
                        Text(part, "Label"),
                        part.GetProperty("Encrypted").GetBoolean(),
                        part.TryGetProperty("Locked", out JsonElement lockedValue) && lockedValue.ValueKind == JsonValueKind.True,
                        Text(part, "Guid")));
                }
            }

            disks.Add(new CloneDisk(
                disk.GetProperty("Number").GetInt32(),
                Text(disk, "Name"),
                disk.GetProperty("Size").GetUInt64(),
                Text(disk, "Style"),
                disk.GetProperty("Sector").GetInt32(),
                disk.GetProperty("IsBoot").GetBoolean(),
                disk.GetProperty("IsSystem").GetBoolean(),
                Text(disk, "Bus"),
                partitions));
        }

        return disks.OrderBy(disk => disk.Number).ToList();
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty).Trim('\0', ' ') : string.Empty;

    // ---- Whole-disk clone ------------------------------------------------------------------------------------

    public async Task CloneAsync(CloneDisk source, CloneDisk target, ClonePlan plan, CloneOptions options, IProgress<CloneProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!plan.CanStart)
        {
            throw new InvalidOperationException(string.Join("\n", plan.Problems));
        }

        Log(progress, $"Cloning Disk {source.Number} ({source.Name}) to Disk {target.Number} ({target.Name}).");
        if (plan.ShrinkFirst is { } shrink)
        {
            Report(progress, $"Shrinking {shrink.Source.Title} on Disk {source.Number}...", 0, 1);
            await RunPowerShellAsync($"Resize-Partition -DiskNumber {source.Number} -PartitionNumber {shrink.Source.Number} -Size {shrink.Size}", cancellationToken);
            Log(progress, $"{shrink.Source.Title} shrunk to {DiskClonePlanner.FormatSize(shrink.Size)}.");
        }

        Report(progress, $"Preparing Disk {target.Number}...", 0, 1);
        List<TargetPartition> created = await PrepareTargetAsync(target, source.Style, plan.Partitions, eraseDisk: true, cancellationToken);
        Log(progress, $"Disk {target.Number} erased and {created.Count} partitions created.");

        await CopyPartitionsAsync(source, target.Number, plan.Partitions, created, options, progress, cancellationToken);

        foreach ((PlannedPartition planned, TargetPartition partition) in plan.Partitions.Zip(created).Where(pair => pair.First.Grows))
        {
            Report(progress, $"Extending {planned.Source.Title} to {DiskClonePlanner.FormatSize(planned.Size)}...", 1, 1);
            await RunPowerShellAsync($"Update-Disk -Number {target.Number}; Start-Sleep -Seconds 2; Resize-Partition -DiskNumber {target.Number} -PartitionNumber {partition.Number} -Size {planned.Size}", cancellationToken);
            Log(progress, $"{planned.Source.Title} extended to {DiskClonePlanner.FormatSize(planned.Size)}.");
        }

        if (options.MakeBootable && source.HasWindows)
        {
            Report(progress, "Making the new disk start Windows...", 1, 1);
            await MakeBootableAsync(source, target, plan.Partitions, created, progress, cancellationToken);
        }

        Report(progress, "Done", 1, 1);
        Log(progress, $"Clone finished. Shut down, then start from Disk {target.Number} ({target.Name}): swap the disks, or choose it first in the BIOS/UEFI boot menu.");
    }

    // ---- One partition ---------------------------------------------------------------------------------------

    /// <param name="letter">Drive letter for the copy: null for the next free letter, '-' for none.</param>
    public async Task CopyPartitionAsync(CloneDisk sourceDisk, CloneDisk target, ClonePlan plan, CloneTargetSpace space, char? letter,
        IProgress<CloneProgress> progress, CancellationToken cancellationToken)
    {
        if (!plan.CanStart)
        {
            throw new InvalidOperationException(string.Join("\n", plan.Problems));
        }

        PlannedPartition planned = plan.Partitions[0];
        Log(progress, $"Copying {planned.Source.Title} from Disk {sourceDisk.Number} to Disk {target.Number}.");
        if (space.Replaces is { } replaced)
        {
            await RunPowerShellAsync($"Remove-Partition -DiskNumber {target.Number} -PartitionNumber {replaced.Number} -Confirm:$false", cancellationToken);
            Log(progress, $"Removed {replaced.Title} from Disk {target.Number}.");
        }

        List<TargetPartition> created = await PrepareTargetAsync(target, sourceDisk.Style, plan.Partitions, eraseDisk: false, cancellationToken);
        await CopyPartitionsAsync(sourceDisk, target.Number, plan.Partitions, created, new CloneOptions(true, false, true), progress, cancellationToken);

        TargetPartition partition = created[0];
        if (planned.Grows)
        {
            await RunPowerShellAsync($"Update-Disk -Number {target.Number}; Start-Sleep -Seconds 2; Resize-Partition -DiskNumber {target.Number} -PartitionNumber {partition.Number} -Size {planned.Size}", cancellationToken);
            Log(progress, $"Extended to {DiskClonePlanner.FormatSize(planned.Size)}.");
        }

        // Windows may already have given the copy a letter when it came back online.
        string current = $"$p = Get-Partition -DiskNumber {target.Number} -PartitionNumber {partition.Number}; $l = [string]$p.DriveLetter; $has = $l.Trim([char]0).Length -gt 0";
        string apply = letter switch
        {
            '-' => "if ($has) { Remove-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -AccessPath ($l.Trim([char]0) + ':\\') }",
            char chosen => $"if ($has) {{ Set-Partition -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -NewDriveLetter '{chosen}' }} else {{ Add-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -AccessPath '{chosen}:\\' }}",
            null => "if (-not $has) { Add-PartitionAccessPath -DiskNumber $p.DiskNumber -PartitionNumber $p.PartitionNumber -AssignDriveLetter }"
        };
        await RunPowerShellAsync($"Update-Disk -Number {target.Number}; Start-Sleep -Seconds 2; {current}; {apply}", cancellationToken);

        Report(progress, "Done", 1, 1);
        Log(progress, "Partition copied.");
    }

    // ---- Steps ---------------------------------------------------------------------------------------------------

    private sealed record TargetPartition(int Number, string VolumePath, string Guid, ulong Offset);

    /// <summary>Creates the planned partitions (sized as the source, the extra space is added after the copy) without formatting them.</summary>
    private static async Task<List<TargetPartition>> PrepareTargetAsync(CloneDisk target, string sourceStyle, IReadOnlyList<PlannedPartition> partitions,
        bool eraseDisk, CancellationToken cancellationToken)
    {
        string style = eraseDisk ? sourceStyle : target.Style == "RAW" ? "GPT" : target.Style;
        StringBuilder script = new();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine($"$n = {target.Number}");
        script.AppendLine("$d = Get-Disk -Number $n");
        if (eraseDisk)
        {
            script.AppendLine("if ($d.IsBoot -or $d.IsSystem) { throw 'Refusing to erase the disk Windows runs from.' }");
        }
        script.AppendLine("if ($d.IsOffline) { Set-Disk -Number $n -IsOffline $false }");
        script.AppendLine("if ($d.IsReadOnly) { Set-Disk -Number $n -IsReadOnly $false }");
        if (eraseDisk)
        {
            script.AppendLine("if ([string]$d.PartitionStyle -ne 'RAW') { Clear-Disk -Number $n -RemoveData -RemoveOEM -Confirm:$false }");
            script.AppendLine($"Initialize-Disk -Number $n -PartitionStyle {style}");
            // Windows may add its own reserved partition when initializing; the clone's layout replaces it.
            script.AppendLine("Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Remove-Partition -Confirm:$false -ErrorAction SilentlyContinue");
        }
        else if (target.Style == "RAW")
        {
            script.AppendLine("Initialize-Disk -Number $n -PartitionStyle GPT");
            script.AppendLine("Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Remove-Partition -Confirm:$false -ErrorAction SilentlyContinue");
        }

        script.AppendLine("$created = @()");
        List<string> diskpart = new();
        foreach (PlannedPartition planned in partitions)
        {
            ulong size = planned.Grows ? planned.Source.Size : planned.Size;
            if (style == "GPT")
            {
                string type = planned.Source.GptType.StartsWith('{') ? planned.Source.GptType : DiskClonePlanner.BasicDataType;
                script.AppendLine($"$created += New-Partition -DiskNumber $n -Offset {planned.Offset} -Size {size} -GptType '{type}'");
            }
            else
            {
                bool fat = planned.Source.FileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase);
                script.AppendLine($"$p = New-Partition -DiskNumber $n -Offset {planned.Offset} -Size {size} -MbrType {(fat ? "FAT32" : "IFS")}");
                if (planned.Source.IsActive && eraseDisk)
                {
                    script.AppendLine("Set-Partition -DiskNumber $n -PartitionNumber $p.PartitionNumber -IsActive $true");
                }

                script.AppendLine("$created += $p");
            }
        }

        script.AppendLine("Update-Disk -Number $n");
        script.AppendLine("Start-Sleep -Seconds 2");
        script.AppendLine("$created | ForEach-Object { $q = Get-Partition -DiskNumber $n -PartitionNumber $_.PartitionNumber; [pscustomobject]@{ Number = [int]$q.PartitionNumber; Offset = [uint64]$q.Offset; Guid = [string]$q.Guid; Paths = @($q.AccessPaths) } } | ConvertTo-Json -Compress -Depth 3");
        string json = await RunPowerShellAsync(script.ToString(), cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json.Trim().StartsWith('[') ? json.Trim() : $"[{json.Trim()}]");
        List<TargetPartition> created = document.RootElement.EnumerateArray().Select(part => new TargetPartition(
                part.GetProperty("Number").GetInt32(),
                part.GetProperty("Paths").ValueKind == JsonValueKind.Array
                    ? part.GetProperty("Paths").EnumerateArray().Select(path => path.GetString() ?? string.Empty).FirstOrDefault(path => path.StartsWith(@"\\?\Volume", StringComparison.OrdinalIgnoreCase)) ?? string.Empty
                    : string.Empty,
                Text(part, "Guid"),
                part.GetProperty("Offset").GetUInt64()))
            .ToList();

        // Attributes New-Partition cannot set: recovery partitions are "required, no drive letter"; MBR types other than NTFS/FAT32.
        foreach ((PlannedPartition planned, TargetPartition partition) in partitions.Zip(created))
        {
            if (style == "GPT" && planned.Source.IsRecovery)
            {
                diskpart.Add($"select disk {target.Number}\nselect partition {partition.Number}\ngpt attributes=0x8000000000000001");
            }
            else if (style == "MBR" && planned.Source.MbrType is not (0 or 0x07 or 0x0B or 0x0C))
            {
                diskpart.Add($"select disk {target.Number}\nselect partition {partition.Number}\nset id={planned.Source.MbrType:X2} override");
            }
        }

        if (diskpart.Count > 0)
        {
            await RunDiskPartAsync(string.Join("\n", diskpart), cancellationToken);
        }

        if (created.Count != partitions.Count)
        {
            throw new InvalidOperationException($"Windows created {created.Count} of {partitions.Count} partitions on Disk {target.Number}.");
        }

        return created;
    }

    /// <param name="useSnapshots">False for a source nothing writes to (an attached backup image), which is read directly.</param>
    /// <param name="onlyChanged">Write only the pieces that differ from what the target already holds (an incremental image).</param>
    private static async Task CopyPartitionsAsync(CloneDisk source, int targetDisk, IReadOnlyList<PlannedPartition> partitions, IReadOnlyList<TargetPartition> created,
        CloneOptions options, IProgress<CloneProgress> progress, CancellationToken cancellationToken, bool useSnapshots = true, bool onlyChanged = false)
    {
        List<(PlannedPartition Planned, TargetPartition Target)> work = partitions.Zip(created).Where(pair => pair.First.Mode != CopyMode.Skip).ToList();
        CopyTracker tracker = new(work.Sum(pair => (long)(options.UsedSpaceOnly ? pair.Planned.BytesToCopy : Math.Min(pair.Planned.Source.Size, pair.Planned.Size))));
        List<Snapshot> snapshots = new();
        try
        {
            foreach ((PlannedPartition planned, TargetPartition target) in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using CopySource from = OpenSource(source, planned, options.UsedSpaceOnly, useSnapshots, snapshots, tracker, progress);
                (SafeFileHandle writer, long targetBase) = OpenTarget(target, targetDisk);
                using SafeFileHandle targetHandle = writer;
                await CopyAndCheckAsync(planned.Source.Title, from, writer, targetBase, options.Verify, tracker, progress, cancellationToken, onlyChanged);
            }
        }
        finally
        {
            foreach (Snapshot shot in snapshots)
            {
                shot.Delete();
            }
        }
    }

    /// <summary>Progress of a copy of several partitions: bytes done so far, the total, and the clock for the speed.</summary>
    private sealed class CopyTracker
    {
        public CopyTracker(long total) => Total = total;

        public long Total { get; }
        public long Done { get; set; }
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
    }

    /// <summary>A partition opened for reading: the handle, the partition's offset in it, and the byte ranges to copy.</summary>
    private sealed class CopySource : IDisposable
    {
        public CopySource(SafeFileHandle reader, long sourceBase, Snapshot? snapshot, List<(long Offset, long Length)> ranges)
        {
            Reader = reader;
            Base = sourceBase;
            Snapshot = snapshot;
            Ranges = ranges;
        }

        public SafeFileHandle Reader { get; }
        public long Base { get; }
        public Snapshot? Snapshot { get; }
        public List<(long Offset, long Length)> Ranges { get; }

        public void Dispose() => Reader.Dispose();
    }

    /// <summary>
    /// Opens a partition of a live disk for copying: through a snapshot when it is in use (so Windows keeps running),
    /// through its volume, or through the disk at its offset when Windows gives it no volume. With used-only, the
    /// ranges are the clusters NTFS has in use.
    /// </summary>
    private static CopySource OpenSource(CloneDisk source, PlannedPartition planned, bool usedOnly, bool useSnapshots, List<Snapshot> snapshots, CopyTracker tracker,
        IProgress<CloneProgress> progress)
    {
        long length = (long)Math.Min(planned.Source.Size, planned.Size);
        string sourcePath;
        long sourceBase = 0;
        Snapshot? shot = null;
        if (useSnapshots && planned.Mode is CopyMode.SnapshotUsed or CopyMode.SnapshotFull)
        {
            Report(progress, $"Taking a snapshot of {planned.Source.Title}...", tracker.Done, tracker.Total, tracker.Clock);
            try
            {
                // Files written moments ago may still be partly in Windows' cache; the snapshot would carry the old
                // sectors. Writing the cache out first makes the snapshot show what the files hold now.
                Native.FlushVolume(planned.Source.VolumePath);
                shot = Snapshot.Create(planned.Source.VolumePath);
                snapshots.Add(shot);
                Log(progress, $"Snapshot of {planned.Source.Title} taken.");
            }
            catch (Exception exception) when (planned.Source.Letter is null && exception is InvalidOperationException or ManagementException)
            {
                // Small partitions without a letter (recovery) often have too little room for a snapshot; nothing
                // writes to them while Windows runs, so they are read directly.
                Log(progress, $"{planned.Source.Title}: no snapshot possible ({exception.Message}); reading it directly.");
            }
        }

        if (shot is not null)
        {
            sourcePath = shot.DevicePath;
        }
        else if (planned.Source.VolumePath.Length > 0)
        {
            sourcePath = planned.Source.VolumePath.TrimEnd('\\');
        }
        else
        {
            sourcePath = $@"\\.\PhysicalDrive{source.Number}";
            sourceBase = (long)planned.Source.Offset;
        }

        SafeFileHandle reader = Native.Open(sourcePath, write: false);
        Native.AllowExtendedDasdIo(reader);
        List<(long Offset, long Length)> ranges = new() { (0, length) };
        if (usedOnly && planned.Mode == CopyMode.SnapshotUsed && Native.TryGetUsedRanges(reader, length, out List<(long, long)> used))
        {
            ranges = used;
            // Only when copying from a running Windows: a restore copies an image as it is, so what the image holds
            // in those files' place (nothing) lands on the disk, not what the disk held there before.
            int bytesPerCluster = useSnapshots ? Native.BytesPerCluster(reader) : 0;
            if (bytesPerCluster > 0)
            {
                Native.EnableBackupPrivilege(); // the hibernation file and the snapshot storage are otherwise SYSTEM's alone
                List<(long Offset, long Length)> leftOut = new();
                List<string> problems = new();
                List<string> files = FilesLeftOut(sourcePath, out string? listing);
                foreach (string file in files)
                {
                    leftOut.AddRange(Native.ExtentsOf(file, bytesPerCluster, out string? problem));
                    if (problem is not null)
                    {
                        problems.Add($"{Path.GetFileName(file)} {problem}");
                    }
                }

                if (listing is not null)
                {
                    problems.Add(listing);
                }

                if (leftOut.Count > 0)
                {
                    ranges = DiskClonePlanner.Subtract(ranges, leftOut);
                    Log(progress, $"{planned.Source.Title}: {DiskClonePlanner.FormatSize((ulong)leftOut.Sum(extent => extent.Length))} of page, hibernation, swap and snapshot-storage files left out.");
                }

                if (problems.Count > 0)
                {
                    Log(progress, $"{planned.Source.Title}: copied although not needed: {string.Join("; ", problems)}.");
                }
            }
        }

        return new CopySource(reader, sourceBase, shot, ranges);
    }

    /// <summary>
    /// Opens a new partition for writing: normally through its own volume (locked); a partition Windows gives no
    /// volume (often an EFI partition) is written through the disk at its offset.
    /// </summary>
    private static (SafeFileHandle Writer, long Base) OpenTarget(TargetPartition target, int targetDisk)
    {
        if (target.VolumePath.Length > 0)
        {
            SafeFileHandle writer = Native.Open(target.VolumePath.TrimEnd('\\'), write: true);
            Native.LockAndDismount(writer);
            Native.AllowExtendedDasdIo(writer);
            return (writer, 0);
        }

        return (Native.Open($@"\\.\PhysicalDrive{targetDisk}", write: true), (long)target.Offset);
    }

    /// <summary>Copies one partition's ranges and, when asked, compares samples of the copy with the original.</summary>
    private static async Task CopyAndCheckAsync(string title, CopySource from, SafeFileHandle writer, long targetBase, bool verify, CopyTracker tracker,
        IProgress<CloneProgress> progress, CancellationToken cancellationToken, bool onlyChanged = false)
    {
        long partitionDone = 0;
        long partitionTotal = from.Ranges.Sum(range => range.Length);
        long lastReport = 0;
        long written = await Task.Run(() => CopyRanges(from.Reader, from.Base, writer, targetBase, from.Ranges, copied =>
        {
            partitionDone += copied;
            // At most four updates a second, so the window stays responsive.
            if (tracker.Clock.ElapsedMilliseconds - lastReport >= 250 || partitionDone >= partitionTotal)
            {
                lastReport = tracker.Clock.ElapsedMilliseconds;
                Report(progress, $"{(onlyChanged ? "Comparing" : "Copying")} {title}", tracker.Done + Math.Min(partitionDone, partitionTotal), tracker.Total, tracker.Clock);
            }
        }, cancellationToken, onlyChanged), cancellationToken);

        tracker.Done += partitionTotal;
        Log(progress, onlyChanged
            ? $"{title}: {DiskClonePlanner.FormatSize((ulong)partitionTotal)} compared, {DiskClonePlanner.FormatSize((ulong)written)} changed and copied."
            : $"{title}: {DiskClonePlanner.FormatSize((ulong)partitionTotal)} copied.");
        if (verify)
        {
            Report(progress, $"Checking the copy of {title}...", tracker.Done, tracker.Total, tracker.Clock);
            Func<IReadOnlyList<long>, Dictionary<long, List<string>>?>? ownersOf = from.Snapshot is not null ? Native.OwnerLookup(from.Reader) : null;
            string? difference = await Task.Run(() => Verify(from.Reader, from.Base, writer, targetBase, from.Ranges, ownersOf), cancellationToken);
            if (difference is not null)
            {
                throw new InvalidOperationException($"The copy of {title} differs from the original ({difference}). Do not use the copy; try again.");
            }

            Log(progress, $"{title}: copy checked.");
        }
    }

    // ---- Backup to an image file -----------------------------------------------------------------------------------

    /// <summary>
    /// Backs up the planned partitions of a disk into a new VHDX image in a folder (another partition, an external disk
    /// or a network share) and writes the description file next to it. Windows makes the image (a dynamic virtual
    /// disk, which grows only with the data written), it is attached as a disk, and the partitions are cloned onto
    /// it exactly as onto a new SSD. Returns the backup.
    /// </summary>
    /// <param name="parent">
    /// For an incremental backup: the image to build on. The new image is a differencing disk that stores only what
    /// changed since; it needs the parent (and the parent's chain) in the same folder. Throws
    /// <see cref="IncrementalNotPossibleException"/> when the disk's partitions no longer match the parent's, so the
    /// caller can make a full backup instead.
    /// </param>
    /// <summary>
    /// Makes the image file with 1 MB blocks (see <see cref="Vhdx.ImageBlockSize"/>) instead of diskpart's 32 MB ones,
    /// so an incremental image built on it grows only by about what changed. False when Windows refused (it then is
    /// made by diskpart as before), with the reason logged.
    /// </summary>
    private static bool TryCreateWithSmallBlocks(string imagePath, ulong virtualSize, IProgress<CloneProgress> progress, string? parentPath = null)
    {
        try
        {
            Vhdx.Create(imagePath, virtualSize, parentPath: parentPath);
            return true;
        }
        catch (IOException exception)
        {
            Log(progress, $"The image could not be made with small blocks ({exception.Message}); it is made the usual way, and incremental backups built on it will be larger.");
            TryDelete(imagePath);
            return false;
        }
    }

    public async Task<BackupImage> BackupAsync(CloneDisk source, BackupPlan plan, string folder, bool verify, IProgress<CloneProgress> progress,
        CancellationToken cancellationToken, BackupImage? parent = null)
    {
        if (!plan.CanStart)
        {
            throw new InvalidOperationException(string.Join("\n", plan.Problems));
        }

        if (parent is not null && !parent.ChainComplete())
        {
            throw new IncrementalNotPossibleException($"{parent.ImageFileName} or an image it builds on is missing.");
        }

        Directory.CreateDirectory(folder);
        string baseName = $"{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd-HHmm}" + (parent is null ? string.Empty : "-inc");
        string imagePath = Path.Combine(folder, baseName + BackupImage.ImageExtension);
        for (int attempt = 2; File.Exists(imagePath) || File.Exists(BackupImage.DescriptionPathFor(imagePath)); attempt++)
        {
            imagePath = Path.Combine(folder, $"{baseName}-{attempt}{BackupImage.ImageExtension}");
        }

        Log(progress, parent is null
            ? $"Backing up Disk {source.Number} ({source.Name}) to {imagePath}."
            : $"Backing up what changed on Disk {source.Number} ({source.Name}) since {parent.Title} to {imagePath}.");
        Report(progress, "Creating the image file...", 0, 1);
        ulong megabytes = (source.Size + DiskClonePlanner.MiB - 1) / DiskClonePlanner.MiB;
        string create = parent is null
            ? $"create vdisk file=\"{imagePath}\" maximum={megabytes} type=expandable"
            : $"create vdisk file=\"{imagePath}\" parent=\"{parent.ImagePath}\"";
        if (TryCreateWithSmallBlocks(imagePath, megabytes * DiskClonePlanner.MiB, progress, parent?.ImagePath))
        {
            create = $"select vdisk file=\"{imagePath}\"";
        }

        try
        {
            await RunDiskPartAsync($"{create}\nattach vdisk", cancellationToken);
        }
        catch (InvalidOperationException exception) when (imagePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Windows could not create the image on the network share. Check that the share allows large files and that you can write there; otherwise back up to a local or external disk and copy the files to the share afterwards. ({exception.Message})");
        }

        DateTime created = DateTime.UtcNow;
        try
        {
            int number = await FindVirtualDiskNumberAsync(imagePath, source.Size, cancellationToken);
            CloneDisk virtualDisk = (await GetDisksAsync(cancellationToken)).First(disk => disk.Number == number);
            Log(progress, $"Image created and attached as Disk {number}.");
            List<TargetPartition> partitions;
            if (parent is null)
            {
                partitions = await PrepareTargetAsync(virtualDisk, source.Style, plan.Partitions, eraseDisk: true, cancellationToken);
            }
            else
            {
                // The differencing image already holds the parent's partitions; they must be the ones to copy into.
                partitions = MatchPartitions(virtualDisk, plan.Partitions)
                    ?? throw new IncrementalNotPossibleException($"the partitions of Disk {source.Number} have changed since {parent.Title}.");
            }

            await CopyPartitionsAsync(source, number, plan.Partitions, partitions, new CloneOptions(true, false, verify), progress, cancellationToken, onlyChanged: parent is not null);
        }
        catch
        {
            await TryDetachAsync(imagePath);
            TryDelete(imagePath);
            throw;
        }

        Report(progress, "Closing the image...", 1, 1);
        await RunDiskPartAsync($"select vdisk file=\"{imagePath}\"\ndetach vdisk", cancellationToken);
        BackupImage image = new(BackupImage.CurrentFormatVersion, created, Environment.MachineName, Path.GetFileName(imagePath), source.Name, source.Size,
            source.Style, source.LogicalSectorSize, verify,
            plan.Partitions.Select(planned => new BackupPartition(planned.Source with { IsEncrypted = false, IsLocked = false }, (long)planned.BytesToCopy)).ToList(),
            parent?.ImageFileName ?? string.Empty, parent?.ChainBase ?? Path.GetFileName(imagePath))
        { ImagePath = imagePath };
        image.Save(BackupImage.DescriptionPathFor(imagePath));
        Report(progress, "Done", 1, 1);
        Log(progress, $"Backup finished: {imagePath} ({DiskClonePlanner.FormatSize((ulong)new FileInfo(imagePath).Length)} on disk). Keep the .backup.json file next to it"
            + (parent is null ? "." : $", and keep {parent.ImageFileName} and the images it builds on in the same folder."));
        return image;
    }

    /// <summary>The attached image's partitions for the planned ones (same place, size and kind), or null when they differ.</summary>
    private static List<TargetPartition>? MatchPartitions(CloneDisk attached, IReadOnlyList<PlannedPartition> planned)
    {
        if (attached.Partitions.Count != planned.Count)
        {
            return null;
        }

        List<TargetPartition> matched = new();
        foreach (PlannedPartition plan in planned)
        {
            ClonePartition? partition = attached.Partitions.FirstOrDefault(each => each.Offset == plan.Offset && each.Size == plan.Source.Size
                && each.GptType.Equals(plan.Source.GptType, StringComparison.OrdinalIgnoreCase) && each.MbrType == plan.Source.MbrType);
            if (partition is null)
            {
                return null;
            }

            matched.Add(new TargetPartition(partition.Number, partition.VolumePath, partition.Guid, partition.Offset));
        }

        return matched;
    }

    /// <summary>
    /// Restores a backup onto a disk, which is erased: the image is attached read-only and cloned onto the disk, which
    /// is then made to start Windows when the backup holds it. The plan given was made from the description; it is
    /// made again from the attached image, which has the same partitions.
    /// </summary>
    public async Task RestoreAsync(BackupImage image, CloneDisk target, ClonePlan plan, bool makeBootable, bool verify, IProgress<CloneProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!plan.CanStart)
        {
            throw new InvalidOperationException(string.Join("\n", plan.Problems));
        }

        if (plan.ShrinkFirst is not null)
        {
            throw new InvalidOperationException($"Disk {target.Number} is smaller than the backed-up disk; a backup cannot be shrunk. Restore to a disk of at least {DiskClonePlanner.FormatSize(image.DiskSize)}.");
        }

        if (!File.Exists(image.ImagePath))
        {
            throw new FileNotFoundException($"The backup image was not found: {image.ImagePath}");
        }

        Log(progress, $"Restoring the backup of {image.Title} to Disk {target.Number} ({target.Name}).");
        Report(progress, "Opening the image...", 0, 1);
        await RunDiskPartAsync($"select vdisk file=\"{image.ImagePath}\"\nattach vdisk readonly", cancellationToken);
        try
        {
            int number = await FindVirtualDiskNumberAsync(image.ImagePath, image.DiskSize, cancellationToken);
            CloneDisk attached = (await GetDisksAsync(cancellationToken)).First(disk => disk.Number == number);

            // The image cannot tell which partition is Windows or what letter it had; the description can.
            CloneDisk source = attached with
            {
                Name = $"backup of {image.Title}",
                Partitions = attached.Partitions.Select(partition =>
                {
                    ClonePartition? described = image.Partitions.Select(stored => stored.Partition).FirstOrDefault(stored => stored.Offset == partition.Offset);
                    return described is null ? partition : partition with
                    {
                        IsBoot = described.IsBoot, IsSystem = described.IsSystem, IsActive = described.IsActive, Letter = described.Letter,
                        Label = partition.Label.Length > 0 ? partition.Label : described.Label
                    };
                }).ToList()
            };
            Log(progress, $"Image attached as Disk {number}.");

            ClonePlan live = DiskClonePlanner.PlanClone(source, target);
            if (!live.CanStart || live.ShrinkFirst is not null)
            {
                throw new InvalidOperationException(live.Problems.Count > 0 ? string.Join("\n", live.Problems) : "The attached image does not fit the disk.");
            }

            Report(progress, $"Preparing Disk {target.Number}...", 0, 1);
            List<TargetPartition> created = await PrepareTargetAsync(target, source.Style, live.Partitions, eraseDisk: true, cancellationToken);
            Log(progress, $"Disk {target.Number} erased and {created.Count} partitions created.");
            await CopyPartitionsAsync(source, target.Number, live.Partitions, created, new CloneOptions(true, false, verify), progress, cancellationToken, useSnapshots: false);

            foreach ((PlannedPartition planned, TargetPartition partition) in live.Partitions.Zip(created).Where(pair => pair.First.Grows))
            {
                Report(progress, $"Extending {planned.Source.Title} to {DiskClonePlanner.FormatSize(planned.Size)}...", 1, 1);
                await RunPowerShellAsync($"Update-Disk -Number {target.Number}; Start-Sleep -Seconds 2; Resize-Partition -DiskNumber {target.Number} -PartitionNumber {partition.Number} -Size {planned.Size}", cancellationToken);
                Log(progress, $"{planned.Source.Title} extended to {DiskClonePlanner.FormatSize(planned.Size)}.");
            }

            if (makeBootable && source.HasWindows)
            {
                Report(progress, "Making the disk start Windows...", 1, 1);
                await MakeBootableAsync(source, target, live.Partitions, created, progress, cancellationToken);
            }
        }
        finally
        {
            await TryDetachAsync(image.ImagePath);
        }

        Report(progress, "Done", 1, 1);
        Log(progress, $"Restore finished. Disk {target.Number} ({target.Name}) now holds the backup of {image.Title}.");
    }

    /// <summary>
    /// Attaches a backup image as a disk so its files can be copied out in Explorer. Returns the drive letters it
    /// got. An image on a network share may have to be copied to a local disk first (Windows refuses some).
    /// </summary>
    public async Task<IReadOnlyList<string>> OpenBackupAsync(BackupImage image, CancellationToken cancellationToken)
    {
        await RunDiskPartAsync($"select vdisk file=\"{image.ImagePath}\"\nattach vdisk", cancellationToken);
        int number = await FindVirtualDiskNumberAsync(image.ImagePath, image.DiskSize, cancellationToken);
        string letters = await RunPowerShellAsync($$"""
            $ErrorActionPreference = 'Stop'
            $n = {{number}}
            if ((Get-Disk -Number $n).IsReadOnly) { Set-Disk -Number $n -IsReadOnly $false }
            foreach ($p in Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue) {
                if ($p.Type -in 'Basic','IFS' -and [string]$p.DriveLetter -eq '') { $p | Add-PartitionAccessPath -AssignDriveLetter -ErrorAction SilentlyContinue }
            }
            Start-Sleep -Seconds 1
            (Get-Partition -DiskNumber $n | Where-Object { [string]$_.DriveLetter -ne '' -and [string]$_.DriveLetter -ne [string][char]0 } | ForEach-Object { "$($_.DriveLetter):" }) -join ' '
            """, cancellationToken);
        return letters.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public Task CloseBackupAsync(BackupImage image, CancellationToken cancellationToken) =>
        RunDiskPartAsync($"select vdisk file=\"{image.ImagePath}\"\ndetach vdisk", cancellationToken);

    /// <summary>The disk number Windows gave an attached image (brought online if needed).</summary>
    private static async Task<int> FindVirtualDiskNumberAsync(string imagePath, ulong diskSize, CancellationToken cancellationToken)
    {
        string number = await RunPowerShellAsync($$"""
            $ErrorActionPreference = 'Stop'
            $path = '{{imagePath.Replace("'", "''")}}'
            $d = $null
            for ($try = 0; $try -lt 15 -and -not $d; $try++) {
                Start-Sleep -Seconds 1
                $d = Get-Disk | Where-Object { [string]$_.Location -eq $path } | Select-Object -First 1
                if (-not $d) { $d = Get-Disk | Where-Object { $_.FriendlyName -like '*Virtual*' -and [uint64]$_.Size -ge {{diskSize}} -and [uint64]$_.Size -le {{diskSize + 2 * DiskClonePlanner.MiB}} } | Sort-Object Number -Descending | Select-Object -First 1 }
            }
            if (-not $d) { throw 'Windows attached the image but it was not found among the disks.' }
            if ($d.IsOffline) { Set-Disk -Number $d.Number -IsOffline $false }
            Start-Sleep -Seconds 1
            [string]$d.Number
            """, cancellationToken);
        return int.Parse(number.Trim(), CultureInfo.InvariantCulture);
    }

    private static async Task TryDetachAsync(string imagePath)
    {
        try
        {
            await RunDiskPartAsync($"select vdisk file=\"{imagePath}\"\ndetach vdisk", CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            ApplicationLogService.WriteException("Disk backup: detaching the image", exception);
        }
    }

    /// <summary>Signs in to a network share (\\server\share\...) with a name and password, for this elevated session.</summary>
    public static async Task ConnectShareAsync(string path, string user, string password, CancellationToken cancellationToken)
    {
        string[] parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) || parts.Length < 2)
        {
            throw new InvalidOperationException(@"A network share looks like \\server\share\folder.");
        }

        string share = $@"\\{parts[0]}\{parts[1]}";
        (int exitCode, string output) = await RunAsync("net.exe", $"use \"{share}\" \"{password}\" /user:\"{user}\" /persistent:no", cancellationToken);
        // 1219: already connected with other credentials; that connection is used.
        if (exitCode != 0 && !output.Contains("1219", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Windows could not connect to {share}: {output}");
        }
    }

    /// <summary>Free space of the drive or share a folder is on (the nearest folder that exists); null when unknown.</summary>
    public static ulong? FreeSpace(string folder)
    {
        string? path = folder.TrimEnd('\\');
        while (path is { Length: > 0 } && !Directory.Exists(path))
        {
            path = Path.GetDirectoryName(path);
        }

        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return Native.GetDiskFreeSpaceEx(path + "\\", out ulong free, out _, out _) ? free : null;
    }

    /// <summary>The "\\?\Volume{...}\" a local folder is on; null for network paths and folders that do not exist yet.</summary>
    public static string? VolumePathOf(string folder)
    {
        string? path = folder.TrimEnd('\\');
        while (path is { Length: > 0 } && !Directory.Exists(path))
        {
            path = Path.GetDirectoryName(path);
        }

        if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        StringBuilder mountPoint = new(260);
        StringBuilder volume = new(60);
        return Native.GetVolumePathName(path + "\\", mountPoint, mountPoint.Capacity) && Native.GetVolumeNameForVolumeMountPoint(mountPoint.ToString(), volume, volume.Capacity)
            ? volume.ToString()
            : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }


    /// <summary>
    /// Copies the ranges in 4 MB pieces through a sector-aligned buffer. With <paramref name="onlyChanged"/>, each
    /// piece is first read from the target and written only when it differs, so a differencing image stores only the
    /// changes. Returns the bytes written.
    /// </summary>
    /// <summary>Set by tests to trace the pieces compared: offset, length, and whether they were written.</summary>
    internal static Action<long, int, bool>? TraceChunk;

    internal static long CopyRanges(SafeFileHandle reader, long sourceBase, SafeFileHandle writer, long targetBase, IReadOnlyList<(long Offset, long Length)> ranges,
        Action<long> copied, CancellationToken cancellationToken, bool onlyChanged = false)
    {
        using AlignedIoBuffer buffer = new(ChunkSize);
        using AlignedIoBuffer existing = new(onlyChanged ? ChunkSize : 4096);
        long written = 0;
        foreach ((long offset, long length) in ranges)
        {
            long position = offset;
            long end = offset + length;
            while (position < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(ChunkSize, end - position);
                ReadFully(reader, buffer.Memory.Span[..count], sourceBase + position);
                if (onlyChanged)
                {
                    // Compared in slices that end on the image's block boundaries, so a small change costs one block.
                    int have = ReadUpTo(writer, existing.Memory.Span[..count], targetBase + position);
                    for (int start = 0; start < count;)
                    {
                        int slice = (int)Math.Min(count - start, SliceSize - (targetBase + position + start) % SliceSize);
                        bool same = have >= start + slice && existing.Memory.Span.Slice(start, slice).SequenceEqual(buffer.Memory.Span.Slice(start, slice));
                        TraceChunk?.Invoke(position + start, slice, !same);
                        if (!same)
                        {
                            WriteAndCheck(writer, buffer.Memory.Span.Slice(start, slice), existing.Memory.Span.Slice(start, slice), targetBase + position + start);
                            written += slice;
                        }

                        start += slice;
                    }
                }
                else
                {
                    TraceChunk?.Invoke(position, count, true);
                    RandomAccess.Write(writer, (ReadOnlySpan<byte>)buffer.Memory.Span[..count], targetBase + position);
                    written += count;
                }

                position += count;
                copied(count);
            }
        }

        return written;
    }

    /// <summary>The slices an incremental copy compares and writes: the block size of the images.</summary>
    private const int SliceSize = (int)Vhdx.ImageBlockSize;

    /// <summary>
    /// Writes a slice into a differencing image and reads it back: such an image has been seen to drop part of a piece
    /// written just past where its parent's data ends, so the slice is written again until it holds.
    /// </summary>
    private static void WriteAndCheck(SafeFileHandle writer, ReadOnlySpan<byte> data, Span<byte> readBack, long position)
    {
        for (int attempt = 1; ; attempt++)
        {
            RandomAccess.Write(writer, data, position);
            if (ReadUpTo(writer, readBack, position) == data.Length && readBack.SequenceEqual(data))
            {
                return;
            }

            if (attempt == 3)
            {
                throw new IOException($"The image did not keep what was written at {DiskClonePlanner.FormatSize((ulong)position)}; the backup is not usable. Try again, or make a full backup.");
            }
        }
    }

    /// <summary>Reads as much as there is (a short read at the end of a volume is not an error here).</summary>
    private static int ReadUpTo(SafeFileHandle handle, Span<byte> buffer, long position)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int got = RandomAccess.Read(handle, buffer[read..], position + read);
            if (got <= 0)
            {
                break;
            }

            read += got;
        }

        return read;
    }

    private const string VssStoreId = "{3808876b-c176-4e48-b7ae-04046e6cc752}";

    /// <summary>
    /// Files on a volume not worth copying: Windows rebuilds the page, hibernation and swap files, and the storage of
    /// other snapshots (restore points) is of no use on another disk and changes with every write. Their clusters are
    /// left out of a copy, which keeps an image smaller and an incremental image free of their constant changes.
    /// </summary>
    internal static List<string> FilesLeftOut(string volumeRoot, out string? problem)
    {
        problem = null;
        List<string> files = new() { Path.Combine(volumeRoot, "pagefile.sys"), Path.Combine(volumeRoot, "hiberfil.sys"), Path.Combine(volumeRoot, "swapfile.sys") };
        string storage = Path.Combine(volumeRoot, "System Volume Information");
        try
        {
            if (Directory.Exists(storage))
            {
                files.AddRange(Directory.EnumerateFiles(storage).Where(path => Path.GetFileName(path).Contains(VssStoreId, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problem = $"the snapshot storage cannot be listed ({exception.Message.TrimEnd('.')})";
        }

        return files;
    }

    /// <summary>
    /// Files a snapshot does not freeze: reading them from a snapshot gives what Windows writes there now, so their copy
    /// can differ from a later read without anything being wrong (the page, hibernation and swap files, and the
    /// storage of other snapshots such as restore points).
    /// </summary>
    internal static bool ChangesDuringSnapshot(string stream)
    {
        string path = stream.Split(':')[0];
        return path.Equals(@"\pagefile.sys", StringComparison.OrdinalIgnoreCase)
            || path.Equals(@"\hiberfil.sys", StringComparison.OrdinalIgnoreCase)
            || path.Equals(@"\swapfile.sys", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\System Volume Information\", StringComparison.OrdinalIgnoreCase) && path.Contains(VssStoreId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compares up to 32 pieces spread over the copied ranges; returns where they differ, or null. With
    /// <paramref name="ownersOf"/> (the files owning byte offsets), differences inside files a snapshot does not freeze
    /// are not counted.
    /// </summary>
    internal static string? Verify(SafeFileHandle reader, long sourceBase, SafeFileHandle writer, long targetBase, IReadOnlyList<(long Offset, long Length)> ranges,
        Func<IReadOnlyList<long>, Dictionary<long, List<string>>?>? ownersOf = null)
    {
        const int piece = 1024 * 1024;
        using AlignedIoBuffer original = new(piece);
        using AlignedIoBuffer copy = new(piece);
        Random random = new(7);
        List<(long Offset, long Length)> candidates = ranges.Where(range => range.Length >= 4096).ToList();
        for (int sample = 0; sample < Math.Min(32, candidates.Count * 4); sample++)
        {
            (long offset, long length) = candidates[random.Next(candidates.Count)];
            int size = (int)Math.Min(piece, length);
            size -= size % 4096;
            if (size == 0)
            {
                continue;
            }

            long start = offset + (length > size ? random.NextInt64((length - size) / 4096 + 1) * 4096 : 0);
            ReadFully(reader, original.Memory.Span[..size], sourceBase + start);
            ReadFully(writer, copy.Memory.Span[..size], targetBase + start);
            List<long> differing = new();
            for (int block = 0; block < size; block += 4096)
            {
                if (!original.Memory.Span.Slice(block, 4096).SequenceEqual(copy.Memory.Span.Slice(block, 4096)))
                {
                    differing.Add(start + block);
                }
            }

            if (differing.Count == 0)
            {
                continue;
            }

            // A difference counts unless every differing block belongs to a file a snapshot does not freeze.
            Dictionary<long, List<string>>? owners = ownersOf?.Invoke(differing);
            long real = differing.FirstOrDefault(block =>
                owners is null || !owners.TryGetValue(block, out List<string>? files) || files.Count == 0 || !files.All(ChangesDuringSnapshot), -1);
            if (real >= 0)
            {
                string? file = owners is not null && owners.TryGetValue(real, out List<string>? found) ? found.FirstOrDefault() : null;
                return $"at {DiskClonePlanner.FormatSize((ulong)real)}" + (file is null ? string.Empty : $", in {file.Split(':')[0]}");
            }
        }

        return null;
    }

    private static void ReadFully(SafeFileHandle handle, Span<byte> buffer, long position)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int got = RandomAccess.Read(handle, buffer[read..], position + read);
            if (got <= 0)
            {
                throw new IOException($"Reading stopped early at {DiskClonePlanner.FormatSize((ulong)(position + read))}.");
            }

            read += got;
        }
    }

    /// <summary>Boot setup at the end of a whole-disk clone, for the copied Windows and boot partitions.</summary>
    private static async Task MakeBootableAsync(CloneDisk source, CloneDisk target, IReadOnlyList<PlannedPartition> partitions,
        IReadOnlyList<TargetPartition> created, IProgress<CloneProgress> progress, CancellationToken cancellationToken)
    {
        int windowsIndex = partitions.ToList().FindIndex(planned => planned.Source.IsBoot);
        int bootIndex = source.Style == "GPT"
            ? partitions.ToList().FindIndex(planned => planned.Source.IsEfi)
            : partitions.ToList().FindIndex(planned => planned.Source.IsActive);
        if (windowsIndex < 0 || bootIndex < 0)
        {
            Log(progress, "No Windows or boot partition was found to set up; the new disk may not start by itself.");
            return;
        }

        await SetUpBootAsync(target.Number, source.Style, created[windowsIndex].Number, created[bootIndex].Number, setActive: false,
            partitions[windowsIndex].Source.Letter ?? 'C', progress, cancellationToken);
    }

    /// <summary>
    /// "Make this disk start Windows" after copying partitions one by one: the same boot setup as a whole-disk clone,
    /// on a disk chosen by the user.
    /// </summary>
    public async Task MakeDiskStartWindowsAsync(CloneDisk disk, BootSetupPlan plan, IProgress<CloneProgress> progress, CancellationToken cancellationToken)
    {
        if (!plan.CanStart)
        {
            throw new InvalidOperationException(string.Join("\n", plan.Problems));
        }

        Report(progress, $"Making Disk {disk.Number} start Windows...", 0, 1);
        await SetUpBootAsync(disk.Number, disk.Style, plan.Windows!.Number, plan.Boot!.Number, plan.SetActive, 'C', progress, cancellationToken);
        Report(progress, "Done", 1, 1);
        Log(progress, $"Disk {disk.Number} is set up to start Windows. Shut down, then start from it (swap the disks, or choose it first in the BIOS/UEFI boot menu).");
    }

    /// <summary>
    /// Turns BitLocker off on partitions so they can be copied, and waits until they are fully decrypted. Works on
    /// partitions without a drive letter (such as a PC maker's recovery partition), which Control Panel does not list.
    /// Decrypting carries on in Windows if the wait is cancelled.
    /// </summary>
    public async Task DecryptAsync(IReadOnlyList<ClonePartition> partitions, IProgress<CloneProgress> progress, CancellationToken cancellationToken)
    {
        foreach (ClonePartition partition in partitions)
        {
            if (partition.VolumePath.Length == 0)
            {
                throw new InvalidOperationException($"{partition.Title} has no volume Windows can decrypt.");
            }

            string volume = $"Get-CimInstance -Namespace root/CIMV2/Security/MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume | Where-Object DeviceID -eq '{partition.VolumePath}'";
            Log(progress, $"Turning BitLocker off on {partition.Title}...");
            string started = await RunPowerShellAsync($$"""
                $v = {{volume}}
                if (-not $v) { throw 'BitLocker does not know this partition.' }
                $r = Invoke-CimMethod -InputObject $v -MethodName Decrypt
                [string]$r.ReturnValue
                """, cancellationToken);
            if (!uint.TryParse(started.Trim(), out uint code) || code != 0)
            {
                throw new InvalidOperationException($"BitLocker could not start decrypting {partition.Title} (error 0x{code:X8}). Try: manage-bde -off {partition.VolumePath}");
            }

            int unreadable = 0;
            while (true)
            {
                // A fully decrypted partition without a letter drops out of BitLocker's list ("gone"), and while it
                // finishes the volume can briefly not answer ("busy").
                string answer = (await RunPowerShellAsync($$"""
                    try {
                        $v = {{volume}}
                        if (-not $v) { 'gone' } else {
                            $s = Invoke-CimMethod -InputObject $v -MethodName GetConversionStatus -ErrorAction Stop
                            "$($s.ConversionStatus) $($s.EncryptionPercentage)"
                        }
                    } catch { 'busy' }
                    """, cancellationToken)).Trim();
                if (answer == "gone")
                {
                    break;
                }

                string[] status = answer.Split(' ');
                if (status.Length == 2 && int.TryParse(status[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int conversion)
                    && double.TryParse(status[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
                {
                    unreadable = 0;
                    if (conversion == 0)
                    {
                        break;
                    }

                    if (conversion is not (3 or 5))
                    {
                        throw new InvalidOperationException($"BitLocker stopped decrypting {partition.Title} (status {conversion}).");
                    }

                    Report(progress, $"Decrypting {partition.Title}...", (long)(partition.Size * (100 - percent) / 100), (long)partition.Size);
                }
                else if (++unreadable >= 10)
                {
                    throw new InvalidOperationException($"BitLocker's progress on {partition.Title} could not be read. Select Refresh disks to see whether it finished.");
                }

                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }

            Log(progress, $"{partition.Title} is decrypted.");
        }

        Report(progress, "Done", 1, 1);
    }

    /// <summary>
    /// Writes new boot files for the Windows on a partition (bcdboot) onto the disk's EFI or active partition, and
    /// sets that Windows' drive letter (C:) to the partition, so the PC starts from the disk with Windows on C:.
    /// Partitions are given a temporary letter only when they have none (Windows may already have given one).
    /// </summary>
    private static async Task SetUpBootAsync(int diskNumber, string style, int windowsPartition, int bootPartition, bool setActive, char windowsLetter,
        IProgress<CloneProgress> progress, CancellationToken cancellationToken)
    {
        if (setActive)
        {
            await RunDiskPartAsync($"select disk {diskNumber}\nselect partition {bootPartition}\nactive", cancellationToken);
            Log(progress, "Windows partition marked active (BIOS start).");
        }

        Dictionary<int, char> letters = await PartitionLettersAsync(diskNumber, cancellationToken);
        Queue<char> free = new(FreeLetters());
        List<(int Partition, char Letter)> assigned = new();
        async Task<char> LetterFor(int partition)
        {
            if (letters.TryGetValue(partition, out char existing))
            {
                return existing;
            }

            char letter = free.Count > 0 ? free.Dequeue() : throw new InvalidOperationException("A free drive letter is needed for a moment to set up the disk.");
            await RunDiskPartAsync($"select disk {diskNumber}\nselect partition {partition}\nassign letter={letter}", cancellationToken);
            assigned.Add((partition, letter));
            letters[partition] = letter;
            return letter;
        }

        try
        {
            char windows = await LetterFor(windowsPartition);
            char boot = await LetterFor(bootPartition);
            if (!File.Exists($@"{windows}:\Windows\System32\config\SYSTEM"))
            {
                throw new InvalidOperationException($"There is no Windows on the partition chosen (no {windows}:\\Windows folder). Choose the partition the Windows partition was copied to.");
            }

            string firmware = style == "GPT" ? "UEFI" : "BIOS";
            (int bcdExit, string bcdOutput) = await RunAsync("bcdboot.exe", $"{windows}:\\Windows /s {boot}: /f {firmware}", cancellationToken);
            if (bcdExit != 0)
            {
                throw new InvalidOperationException($"bcdboot could not write the boot files: {bcdOutput.Trim()}");
            }

            Log(progress, $"Boot files written to the disk ({firmware}).");
            await CleanCopiedBootMenuAsync(diskNumber, windowsPartition, $@"{boot}:\{(style == "GPT" ? @"EFI\Microsoft\Boot\BCD" : @"Boot\BCD")}", progress, cancellationToken);

            // Give the copied Windows its letter on this partition.
            string details = await RunPowerShellAsync($"$p = Get-Partition -DiskNumber {diskNumber} -PartitionNumber {windowsPartition}; $d = Get-Disk -Number {diskNumber}; '{{0}}|{{1}}|{{2}}' -f $p.Guid, [uint32]$d.Signature, [uint64]$p.Offset", cancellationToken);
            string[] fields = details.Trim().Split('|');
            byte[] value = DiskClonePlanner.MountedDevicesValue(style,
                Guid.TryParse(fields[0], out Guid partitionGuid) ? partitionGuid : Guid.Empty,
                uint.TryParse(fields.ElementAtOrDefault(1), out uint signature) ? signature : 0,
                ulong.TryParse(fields.ElementAtOrDefault(2), out ulong offset) ? offset : 0);
            const string hive = @"HKLM\WUCLONE";
            (int loadExit, string loadOutput) = await RunAsync("reg.exe", $"load {hive} \"{windows}:\\Windows\\System32\\config\\SYSTEM\"", cancellationToken);
            if (loadExit != 0)
            {
                throw new InvalidOperationException($"The copied Windows registry could not be opened: {loadOutput.Trim()}");
            }

            int drivers;
            try
            {
                await RunAsync("reg.exe", $"add {hive}\\MountedDevices /v \"\\DosDevices\\{windowsLetter}:\" /t REG_BINARY /d {Convert.ToHexString(value)} /f", cancellationToken);
                drivers = StartStorageDriversOnAnyHardware(hive[(hive.IndexOf('\\') + 1)..]);
            }
            finally
            {
                await RunAsync("reg.exe", $"unload {hive}", CancellationToken.None);
            }

            Log(progress, $"That Windows starts as {windowsLetter}: from this disk.");
            Log(progress, drivers > 0
                ? $"{drivers} disk-controller and USB drivers set to start with Windows, so the disk also starts in another PC or from a USB enclosure."
                : "The disk-controller and USB drivers already start with Windows.");
        }
        finally
        {
            foreach ((int partition, char letter) in assigned)
            {
                try
                {
                    await RunDiskPartAsync($"select disk {diskNumber}\nselect partition {partition}\nremove letter={letter}", CancellationToken.None);
                }
                catch (InvalidOperationException exception)
                {
                    Log(progress, $"The temporary letter {letter}: could not be removed ({exception.Message}).");
                }
            }
        }
    }

    /// <summary>
    /// The boot store came with the EFI partition from the old disk, so it still lists the old disk's Windows next to
    /// the entry bcdboot just added; started in another PC, it would ask "Choose an operating system" every time.
    /// Every Windows entry that is not on this disk, or that starts the copied Windows a second time, is removed.
    /// A problem here is logged, not fatal: the disk starts either way.
    /// </summary>
    private static async Task CleanCopiedBootMenuAsync(int diskNumber, int windowsPartition, string storePath, IProgress<CloneProgress> progress, CancellationToken cancellationToken)
    {
        try
        {
            string output = await RunPowerShellAsync(
                $"Get-Partition -DiskNumber {diskNumber} | ForEach-Object {{ '{{0}}|{{1}}' -f $_.PartitionNumber, (($_.AccessPaths | Where-Object {{ $_ -like '\\\\?\\Volume*' }}) -join '') }}", cancellationToken);
            Dictionary<int, string> devices = new();
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = line.Split('|');
                if (parts.Length == 2 && int.TryParse(parts[0], out int number) && BootMenu.DeviceNameOf(parts[1]) is string device)
                {
                    devices[number] = device;
                }
            }

            if (!devices.TryGetValue(windowsPartition, out string? windowsDevice))
            {
                Log(progress, "The boot menu was not tidied: the Windows partition's volume was not found.");
                return;
            }

            List<BootMenuEntry> removed = BootMenu.Clean(storePath, windowsDevice,
                partition => devices.Values.Contains(partition, StringComparer.OrdinalIgnoreCase));
            if (removed.Count > 0)
            {
                Log(progress, $"Boot menu tidied: {removed.Count} entr{(removed.Count == 1 ? "y" : "ies")} for the old disk's Windows removed, so the new disk starts without asking which Windows.");
            }
        }
        catch (Exception exception) when (exception is ManagementException or InvalidOperationException or COMException or UnauthorizedAccessException)
        {
            Log(progress, $"The boot menu was not tidied ({exception.Message}); Windows may ask which Windows to start. \"Clean up the start menu\" fixes that from the new disk.");
        }
    }

    /// <summary>
    /// Windows' own disk-controller drivers (SATA, NVMe, RAID, SD, virtual machines) and the USB storage stack. Windows
    /// keeps those the PC it was installed on does not need from loading at start-up (a "StartOverride" entry, and
    /// the USB ones start on demand), so a copy of that Windows cannot find its disk on another PC or in a USB
    /// enclosure and stops with INACCESSIBLE_BOOT_DEVICE. Only the ones present in the copied Windows are touched.
    /// </summary>
    private static readonly string[] BootStorageDrivers =
    {
        "storahci", "stornvme", "storufs", "iaStorV", "iaStorAVC", "iaStorAC", "iaStorVD", "intelide", "pciide", "atapi", "amdsata", "amdxata", "nvstor",
        "LSI_SAS", "LSI_SAS2i", "LSI_SAS3i", "megasas", "megasas2i", "megasas35i", "HpSAMD", "arcsas", "percsas2i", "percsas3i", "smartpqi",
        "storvsc", "vmbus", "vioscsi", "sdbus", "sdstor", "USBSTOR", "UASPStor", "usbccgp", "USBHUB3", "USBXHCI", "usbehci", "usbhub", "usbohci", "usbuhci"
    };

    /// <summary>
    /// In a copied Windows' SYSTEM hive (loaded under HKLM as <paramref name="hiveName"/>): makes every one of
    /// <see cref="BootStorageDrivers"/> that is present start with Windows (Start 0, no StartOverride) in every control
    /// set, and lets Windows start from USB and SD storage (BootDriverFlags, as Windows To Go sets). Returns how many
    /// drivers were changed.
    /// </summary>
    internal static int StartStorageDriversOnAnyHardware(string hiveName)
    {
        using RegistryKey? hive = Registry.LocalMachine.OpenSubKey(hiveName, writable: true);
        return hive is null ? 0 : StartStorageDriversOnAnyHardware(hive);
    }

    /// <summary>The work of <see cref="StartStorageDriversOnAnyHardware(string)"/> on an opened hive (or a key shaped like one).</summary>
    internal static int StartStorageDriversOnAnyHardware(RegistryKey hive)
    {
        int changed = 0;
        foreach (string controlSet in hive.GetSubKeyNames().Where(name => name.StartsWith("ControlSet", StringComparison.OrdinalIgnoreCase)))
        {
            using RegistryKey? services = hive.OpenSubKey($@"{controlSet}\Services", writable: true);
            foreach (string driver in BootStorageDrivers)
            {
                using RegistryKey? service = services?.OpenSubKey(driver, writable: true);
                if (service is null)
                {
                    continue;
                }

                bool touched = false;
                if (service.GetValue("Start") is not 0)
                {
                    service.SetValue("Start", 0, RegistryValueKind.DWord);
                    touched = true;
                }

                if (service.GetSubKeyNames().Contains("StartOverride", StringComparer.OrdinalIgnoreCase))
                {
                    service.DeleteSubKeyTree("StartOverride", throwOnMissingSubKey: false);
                    touched = true;
                }

                if (touched)
                {
                    changed++;
                }
            }

            using RegistryKey? control = hive.OpenSubKey($@"{controlSet}\Control", writable: true);
            if (control is not null)
            {
                const int usbAndSdBoot = 0x14;
                int flags = control.GetValue("BootDriverFlags") is int current ? current : 0;
                if ((flags & usbAndSdBoot) != usbAndSdBoot)
                {
                    control.SetValue("BootDriverFlags", flags | usbAndSdBoot, RegistryValueKind.DWord);
                }
            }
        }

        return changed;
    }

    /// <summary>Drive letters the disk's partitions have now.</summary>
    private static async Task<Dictionary<int, char>> PartitionLettersAsync(int diskNumber, CancellationToken cancellationToken)
    {
        string output = await RunPowerShellAsync(
            $"Get-Partition -DiskNumber {diskNumber} | ForEach-Object {{ '{{0}}|{{1}}' -f $_.PartitionNumber, [string]$_.DriveLetter }}", cancellationToken);
        Dictionary<int, char> letters = new();
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split('|');
            if (parts.Length == 2 && int.TryParse(parts[0], out int number) && parts[1].Trim('\0', ' ') is { Length: > 0 } letter && char.IsLetter(letter[0]))
            {
                letters[number] = char.ToUpperInvariant(letter[0]);
            }
        }

        return letters;
    }
    private static IEnumerable<char> FreeLetters()
    {
        HashSet<char> used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        return Enumerable.Range('R', 'Z' - 'R' + 1).Reverse().Select(code => (char)code).Where(letter => !used.Contains(letter));
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------

    private static void Report(IProgress<CloneProgress> progress, string stage, long done, long total, Stopwatch? clock = null)
    {
        double speed = clock is { Elapsed.TotalSeconds: > 2 } ? done / clock.Elapsed.TotalSeconds : 0;
        progress.Report(new CloneProgress(stage, done, total, speed));
    }

    private static void Log(IProgress<CloneProgress> progress, string line)
    {
        ApplicationLogService.WriteMessage("Disk clone", line);
        progress.Report(new CloneProgress(string.Empty, -1, -1, 0, line));
    }

    /// <summary>Runs a script and returns what it wrote (only its output: progress and error records go to the error stream).</summary>
    private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        script = "$ProgressPreference = 'SilentlyContinue'\n" + script;
        (int exitCode, string output, string error) = await RunWithStreamsAsync("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}", cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(CleanPowerShellError(error.Length > 0 ? error : output));
        }

        return output;
    }

    private static async Task RunDiskPartAsync(string script, CancellationToken cancellationToken)
    {
        string path = Path.Combine(Path.GetTempPath(), $"wu-diskpart-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, script.Replace("\n", "\r\n"), cancellationToken);
        try
        {
            (int exitCode, string output) = await RunAsync("diskpart.exe", $"/s \"{path}\"", cancellationToken);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"diskpart: {output.Trim()}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string arguments, CancellationToken cancellationToken)
    {
        (int exitCode, string output, string error) = await RunWithStreamsAsync(file, arguments, cancellationToken);
        return (exitCode, (output + Environment.NewLine + error).Trim());
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunWithStreamsAsync(string file, string arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{file} could not be started.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, (await output).Trim(), (await error).Trim());
    }

    /// <summary>The PowerShell error message, from plain text or the CLIXML error records an encoded command writes.</summary>
    internal static string CleanPowerShellError(string output)
    {
        if (output.Contains("#< CLIXML", StringComparison.Ordinal))
        {
            List<string> messages = System.Text.RegularExpressions.Regex.Matches(output, "<S S=\"Error\">(.*?)</S>", System.Text.RegularExpressions.RegexOptions.Singleline)
                .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Replace("_x000D__x000A_", " ").Trim())
                .Where(text => text.Length > 0 && !text.StartsWith("At ", StringComparison.Ordinal) && !text.StartsWith("+", StringComparison.Ordinal))
                .ToList();
            return messages.Count > 0 ? string.Join(" ", messages.Take(2)).Trim() : "PowerShell reported an error.";
        }

        return output.Split('\n').Select(text => text.Trim()).FirstOrDefault(text => text.Length > 0 && !text.StartsWith("At ", StringComparison.Ordinal)
            && !text.StartsWith("+", StringComparison.Ordinal)) ?? "PowerShell reported an error.";
    }

    // ---- Volume Shadow Copy --------------------------------------------------------------------------------------

    /// <summary>A Volume Shadow Copy snapshot of one volume, deleted when the copy is done.</summary>
    private sealed class Snapshot
    {
        private Snapshot(string id, string devicePath)
        {
            Id = id;
            DevicePath = devicePath;
        }

        public string Id { get; }

        /// <summary>"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7".</summary>
        public string DevicePath { get; }

        public static Snapshot Create(string volumePath)
        {
            using ManagementClass shadowCopy = new(new ManagementScope(@"\\.\root\cimv2"), new ManagementPath("Win32_ShadowCopy"), null);
            using ManagementBaseObject parameters = shadowCopy.GetMethodParameters("Create");
            parameters["Volume"] = volumePath.EndsWith('\\') ? volumePath : volumePath + "\\";
            parameters["Context"] = "ClientAccessible";
            using ManagementBaseObject result = shadowCopy.InvokeMethod("Create", parameters, null);
            uint code = Convert.ToUInt32(result["ReturnValue"] ?? 1u);
            if (code != 0)
            {
                throw new InvalidOperationException($"Windows could not take a snapshot of the volume (Volume Shadow Copy error {code}). Check that the Volume Shadow Copy service can start and the disk has some free space.");
            }

            string id = result["ShadowID"] as string ?? throw new InvalidOperationException("Windows took a snapshot but did not say which.");
            using ManagementObjectSearcher searcher = new(@"root\cimv2", $"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID = '{id}'");
            string device = searcher.Get().Cast<ManagementObject>().Select(item => item["DeviceObject"] as string).FirstOrDefault(path => path is not null)
                ?? throw new InvalidOperationException("The snapshot could not be found.");
            return new Snapshot(id, device);
        }

        public void Delete()
        {
            try
            {
                using ManagementObjectSearcher searcher = new(@"root\cimv2", $"SELECT * FROM Win32_ShadowCopy WHERE ID = '{Id}'");
                foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
                {
                    using (item)
                    {
                        item.Delete();
                    }
                }
            }
            catch (ManagementException exception)
            {
                ApplicationLogService.WriteException("Disk clone snapshot delete", exception);
            }
        }
    }

    // ---- Raw volume access ------------------------------------------------------------------------------------

    private static class Native
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x1;
        private const uint FileShareWrite = 0x2;
        private const uint OpenExisting = 3;
        private const uint FileFlagNoBuffering = 0x20000000;
        private const uint FileFlagWriteThrough = 0x80000000;
        private const uint FsctlLockVolume = 0x00090018;
        private const uint FsctlDismountVolume = 0x00090020;
        private const uint FsctlAllowExtendedDasdIo = 0x00090083;
        private const uint FsctlGetNtfsVolumeData = 0x00090064;
        private const uint FsctlGetVolumeBitmap = 0x0009006F;
        private const uint FsctlLookupStreamFromCluster = 0x000901FC;
        private const uint FsctlGetRetrievalPointers = 0x00090073;
        private const uint FileReadAttributes = 0x80;
        private const uint FileShareDelete = 0x4;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const int ErrorMoreData = 234;
        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        private const int ErrorHandleEof = 38;
        private const uint TokenAdjustPrivileges = 0x20;
        private const uint TokenQuery = 0x8;
        private const uint SePrivilegeEnabled = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint Low;
            public int High;
        }

        /// <summary>TOKEN_PRIVILEGES with one LUID_AND_ATTRIBUTES.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeFileHandle token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(SafeFileHandle token, bool disableAll, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushFileBuffers(SafeFileHandle handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetDiskFreeSpaceEx(string path, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, int length);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);

        public static SafeFileHandle Open(string path, bool write)
        {
            SafeFileHandle handle = CreateFile(path, write ? GenericRead | GenericWrite : GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero,
                OpenExisting, FileFlagNoBuffering | (write ? FileFlagWriteThrough : 0), IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new IOException($"{path} could not be opened: {new Win32Exception().Message}");
            }

            return handle;
        }

        /// <summary>Writes everything Windows still holds in its cache for a volume to the disk (best effort).</summary>
        public static void FlushVolume(string volumePath)
        {
            using SafeFileHandle volume = CreateFile(volumePath.TrimEnd('\\'), GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (!volume.IsInvalid)
            {
                FlushFileBuffers(volume);
            }
        }

        /// <summary>Takes the new partition for this copy alone, so Windows does not mount or touch it while it is written.</summary>
        public static void LockAndDismount(SafeFileHandle handle)
        {
            for (int attempt = 0; ; attempt++)
            {
                if (DeviceIoControl(handle, FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    break;
                }

                if (attempt >= 10)
                {
                    throw new IOException($"The new partition is in use and could not be locked: {new Win32Exception().Message}");
                }

                Thread.Sleep(500);
            }

            DeviceIoControl(handle, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }

        /// <summary>Lets reads and writes reach the partition's last sectors (NTFS keeps its backup boot sector there).</summary>
        public static void AllowExtendedDasdIo(SafeFileHandle handle) =>
            DeviceIoControl(handle, FsctlAllowExtendedDasdIo, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

        /// <summary>The ranges NTFS has in use, from its volume bitmap (false when it is not NTFS or cannot be read).</summary>
        public static bool TryGetUsedRanges(SafeFileHandle handle, long partitionSize, out List<(long Offset, long Length)> ranges)
        {
            ranges = new List<(long, long)>();
            byte[] volumeData = new byte[512];
            if (!DeviceIoControl(handle, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, volumeData, volumeData.Length, out _, IntPtr.Zero))
            {
                return false;
            }

            long totalClusters = BitConverter.ToInt64(volumeData, 16);
            int bytesPerCluster = BitConverter.ToInt32(volumeData, 44);
            if (totalClusters <= 0 || bytesPerCluster <= 0)
            {
                return false;
            }

            byte[] input = new byte[8]; // starting LCN 0
            byte[] output = new byte[16 + (totalClusters + 7) / 8 + 8];
            if (!DeviceIoControl(handle, FsctlGetVolumeBitmap, input, input.Length, output, output.Length, out int returned, IntPtr.Zero) || returned < 16)
            {
                return false;
            }

            long bitmapClusters = Math.Min(totalClusters, BitConverter.ToInt64(output, 8));
            byte[] bitmap = output[16..];
            ranges = DiskClonePlanner.UsedRanges(bitmap, bitmapClusters, bytesPerCluster, partitionSize);
            return true;
        }

        private static bool _backupPrivilegeTried;

        /// <summary>
        /// Switches on this process's backup privilege (administrators and SYSTEM hold it, switched off). With it, files
        /// opened for backup can be read whatever their permissions say, such as the hibernation file and the snapshot
        /// storage, which only SYSTEM may otherwise touch. Best effort, once per process.
        /// </summary>
        public static void EnableBackupPrivilege()
        {
            if (_backupPrivilegeTried)
            {
                return;
            }

            _backupPrivilegeTried = true;
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out SafeFileHandle token))
            {
                return;
            }

            using (token)
            {
                if (LookupPrivilegeValue(null, "SeBackupPrivilege", out Luid luid))
                {
                    TokenPrivileges privileges = new() { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                    AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }

        /// <summary>The cluster size of an NTFS volume; 0 when it is not NTFS or cannot be read.</summary>
        public static int BytesPerCluster(SafeFileHandle volume)
        {
            byte[] volumeData = new byte[512];
            return DeviceIoControl(volume, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, volumeData, volumeData.Length, out _, IntPtr.Zero)
                ? Math.Max(0, BitConverter.ToInt32(volumeData, 44))
                : 0;
        }

        /// <summary>
        /// Where a file's data lies on its volume, as byte ranges from the volume's start (FSCTL_GET_RETRIEVAL_POINTERS);
        /// empty when the file is missing, cannot be opened, or keeps its data inside the file table. The file is opened
        /// for its attributes only, which works even for the page file Windows holds open.
        /// </summary>
        public static List<(long Offset, long Length)> ExtentsOf(string filePath, int bytesPerCluster, out string? problem)
        {
            problem = null;
            List<(long Offset, long Length)> extents = new();
            using SafeFileHandle file = CreateFile(filePath, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (file.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (error is not (ErrorFileNotFound or ErrorPathNotFound))
                {
                    problem = $"cannot be opened ({new Win32Exception(error).Message.TrimEnd('.')})";
                }

                return extents;
            }

            byte[] input = new byte[8]; // STARTING_VCN_INPUT_BUFFER
            byte[] output = new byte[64 * 1024];
            while (true)
            {
                bool complete = DeviceIoControl(file, FsctlGetRetrievalPointers, input, input.Length, output, output.Length, out int returned, IntPtr.Zero);
                if (!complete && Marshal.GetLastWin32Error() is int error && error != ErrorMoreData)
                {
                    if (error != ErrorHandleEof) // ERROR_HANDLE_EOF: the file has no clusters of its own.
                    {
                        problem = $"its place on the disk cannot be read ({new Win32Exception(error).Message.TrimEnd('.')})";
                    }

                    break;
                }

                // RETRIEVAL_POINTERS_BUFFER: ExtentCount, StartingVcn, then NextVcn and Lcn per extent (Lcn -1: not stored).
                int count = BitConverter.ToInt32(output, 0);
                long vcn = BitConverter.ToInt64(output, 8);
                for (int index = 0; index < count && 32 + 16 * index <= returned; index++)
                {
                    long nextVcn = BitConverter.ToInt64(output, 16 + 16 * index);
                    long lcn = BitConverter.ToInt64(output, 24 + 16 * index);
                    if (lcn >= 0 && nextVcn > vcn)
                    {
                        extents.Add((lcn * bytesPerCluster, (nextVcn - vcn) * bytesPerCluster));
                    }

                    vcn = nextVcn;
                }

                if (complete || count == 0)
                {
                    break;
                }

                BitConverter.TryWriteBytes(input, vcn);
            }

            return extents;
        }

        /// <summary>
        /// Finds the files bytes of an NTFS volume belong to ("\pagefile.sys::$DATA"), as "fsutil volume querycluster"
        /// does: for each byte offset asked, the files owning its cluster (none when it is in no file). Null when the
        /// volume is not NTFS or NTFS cannot tell. NTFS reads its whole file table for each call, so ask for many
        /// offsets at once.
        /// </summary>
        public static Func<IReadOnlyList<long>, Dictionary<long, List<string>>?>? OwnerLookup(SafeFileHandle volume)
        {
            byte[] volumeData = new byte[512];
            if (!DeviceIoControl(volume, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, volumeData, volumeData.Length, out _, IntPtr.Zero))
            {
                return null;
            }

            int bytesPerCluster = BitConverter.ToInt32(volumeData, 44);
            if (bytesPerCluster <= 0)
            {
                return null;
            }

            byte[] output = new byte[1024 * 1024];
            return offsets =>
            {
                // LOOKUP_STREAM_FROM_CLUSTER_INPUT: Flags, NumberOfClusters, Cluster[].
                List<long> clusters = offsets.Select(offset => offset / bytesPerCluster).Distinct().ToList();
                byte[] input = new byte[8 + 8 * clusters.Count];
                BitConverter.TryWriteBytes(input.AsSpan(4), clusters.Count);
                for (int index = 0; index < clusters.Count; index++)
                {
                    BitConverter.TryWriteBytes(input.AsSpan(8 + 8 * index), clusters[index]);
                }

                if (!DeviceIoControl(volume, FsctlLookupStreamFromCluster, input, input.Length, output, output.Length, out int returned, IntPtr.Zero) || returned < 12)
                {
                    return null;
                }

                // LOOKUP_STREAM_FROM_CLUSTER_OUTPUT: Offset (of the first entry), NumberOfMatches, BufferSizeRequired.
                // Each entry: OffsetToNext, Flags, Reserved, Cluster, then the stream's name.
                Dictionary<long, List<string>> owners = clusters.ToDictionary(cluster => cluster * bytesPerCluster, _ => new List<string>());
                int entry = BitConverter.ToInt32(output, 0);
                while (entry > 0 && entry + 24 <= returned)
                {
                    int next = BitConverter.ToInt32(output, entry);
                    long cluster = BitConverter.ToInt64(output, entry + 16);
                    int nameEnd = next > 0 ? Math.Min(entry + next, returned) : returned;
                    string name = Encoding.Unicode.GetString(output, entry + 24, Math.Max(0, nameEnd - entry - 24));
                    int terminator = name.IndexOf('\0');
                    if (owners.TryGetValue(cluster * bytesPerCluster, out List<string>? list))
                    {
                        list.Add(terminator >= 0 ? name[..terminator] : name);
                    }

                    entry = next > 0 ? entry + next : 0;
                }

                // Answered per offset asked (offsets inside one cluster share its owners).
                return offsets.Distinct().ToDictionary(offset => offset, offset => owners[offset / bytesPerCluster * bytesPerCluster]);
            };
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? inBuffer, int inSize, byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize, byte[] outBuffer, int outSize, out int returned, IntPtr overlapped);
    }
}
