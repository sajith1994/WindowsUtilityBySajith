using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CentricDeviceMonitor.Services;

public sealed record CloneOptions(bool UsedSpaceOnly, bool MakeBootable, bool Verify);

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

    private static async Task CopyPartitionsAsync(CloneDisk source, int targetDisk, IReadOnlyList<PlannedPartition> partitions, IReadOnlyList<TargetPartition> created,
        CloneOptions options, IProgress<CloneProgress> progress, CancellationToken cancellationToken)
    {
        List<(PlannedPartition Planned, TargetPartition Target)> work = partitions.Zip(created).Where(pair => pair.First.Mode != CopyMode.Skip).ToList();
        long total = work.Sum(pair => (long)(options.UsedSpaceOnly ? pair.Planned.BytesToCopy : Math.Min(pair.Planned.Source.Size, pair.Planned.Size)));
        long done = 0;
        Stopwatch clock = Stopwatch.StartNew();
        List<Snapshot> snapshots = new();
        try
        {
            foreach ((PlannedPartition planned, TargetPartition target) in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long length = (long)Math.Min(planned.Source.Size, planned.Size);
                string sourcePath;
                long sourceBase = 0;
                bool snapshot = planned.Mode is CopyMode.SnapshotUsed or CopyMode.SnapshotFull;
                Snapshot? shot = null;
                if (snapshot)
                {
                    Report(progress, $"Taking a snapshot of {planned.Source.Title}...", done, total, clock);
                    try
                    {
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

                using SafeFileHandle reader = Native.Open(sourcePath, write: false);
                Native.AllowExtendedDasdIo(reader);
                List<(long Offset, long Length)> ranges = new() { (0, length) };
                if (options.UsedSpaceOnly && planned.Mode == CopyMode.SnapshotUsed && Native.TryGetUsedRanges(reader, length, out List<(long, long)> used))
                {
                    ranges = used;
                }

                // Normally through the new partition's own volume (locked); a partition Windows gives no volume (often
                // an EFI partition) is written through the disk at its offset.
                long targetBase = 0;
                SafeFileHandle writer;
                if (target.VolumePath.Length > 0)
                {
                    writer = Native.Open(target.VolumePath.TrimEnd('\\'), write: true);
                    Native.LockAndDismount(writer);
                    Native.AllowExtendedDasdIo(writer);
                }
                else
                {
                    writer = Native.Open($@"\\.\PhysicalDrive{targetDisk}", write: true);
                    targetBase = (long)target.Offset;
                }

                using SafeFileHandle targetHandle = writer;
                long partitionDone = 0;
                long partitionTotal = ranges.Sum(range => range.Length);
                long lastReport = 0;
                await Task.Run(() => CopyRanges(reader, sourceBase, writer, targetBase, ranges, copied =>
                {
                    partitionDone += copied;
                    // At most four updates a second, so the window stays responsive.
                    if (clock.ElapsedMilliseconds - lastReport >= 250 || partitionDone >= partitionTotal)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        Report(progress, $"Copying {planned.Source.Title}", done + Math.Min(partitionDone, partitionTotal), total, clock);
                    }
                }, cancellationToken), cancellationToken);

                done += partitionTotal;
                Log(progress, $"{planned.Source.Title}: {DiskClonePlanner.FormatSize((ulong)partitionTotal)} copied.");
                if (options.Verify)
                {
                    Report(progress, $"Checking the copy of {planned.Source.Title}...", done, total, clock);
                    string? difference = await Task.Run(() => Verify(reader, sourceBase, writer, targetBase, ranges), cancellationToken);
                    if (difference is not null)
                    {
                        throw new InvalidOperationException($"The copy of {planned.Source.Title} differs from the original ({difference}). Do not use the new disk; try again.");
                    }

                    Log(progress, $"{planned.Source.Title}: copy checked.");
                }
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


    /// <summary>Copies the ranges in 4 MB pieces through a sector-aligned buffer.</summary>
    internal static void CopyRanges(SafeFileHandle reader, long sourceBase, SafeFileHandle writer, long targetBase, IReadOnlyList<(long Offset, long Length)> ranges,
        Action<long> copied, CancellationToken cancellationToken)
    {
        using AlignedIoBuffer buffer = new(ChunkSize);
        foreach ((long offset, long length) in ranges)
        {
            long position = offset;
            long end = offset + length;
            while (position < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(ChunkSize, end - position);
                int read = 0;
                while (read < count)
                {
                    int got = RandomAccess.Read(reader, buffer.Memory.Span[read..count], sourceBase + position + read);
                    if (got <= 0)
                    {
                        throw new IOException($"The source ended early at {DiskClonePlanner.FormatSize((ulong)(position + read))}.");
                    }

                    read += got;
                }

                RandomAccess.Write(writer, (ReadOnlySpan<byte>)buffer.Memory.Span[..count], targetBase + position);
                position += count;
                copied(count);
            }
        }
    }

    /// <summary>Compares up to 32 pieces spread over the copied ranges; returns where they differ, or null.</summary>
    internal static string? Verify(SafeFileHandle reader, long sourceBase, SafeFileHandle writer, long targetBase, IReadOnlyList<(long Offset, long Length)> ranges)
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
            RandomAccess.Read(reader, original.Memory.Span[..size], sourceBase + start);
            RandomAccess.Read(writer, copy.Memory.Span[..size], targetBase + start);
            if (!original.Memory.Span[..size].SequenceEqual(copy.Memory.Span[..size]))
            {
                return $"at {DiskClonePlanner.FormatSize((ulong)start)}";
            }
        }

        return null;
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

            while (true)
            {
                string[] status = (await RunPowerShellAsync($$"""
                    $s = Invoke-CimMethod -InputObject ({{volume}}) -MethodName GetConversionStatus
                    "$($s.ConversionStatus) $($s.EncryptionPercentage)"
                    """, cancellationToken)).Trim().Split(' ');
                int conversion = int.Parse(status[0], System.Globalization.CultureInfo.InvariantCulture);
                double percent = double.Parse(status[1], System.Globalization.CultureInfo.InvariantCulture);
                if (conversion == 0)
                {
                    break;
                }

                if (conversion is not (3 or 5))
                {
                    throw new InvalidOperationException($"BitLocker stopped decrypting {partition.Title} (status {conversion}).");
                }

                Report(progress, $"Decrypting {partition.Title}...", (long)(100 - percent), 100);
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

            try
            {
                await RunAsync("reg.exe", $"add {hive}\\MountedDevices /v \"\\DosDevices\\{windowsLetter}:\" /t REG_BINARY /d {Convert.ToHexString(value)} /f", cancellationToken);
            }
            finally
            {
                await RunAsync("reg.exe", $"unload {hive}", CancellationToken.None);
            }

            Log(progress, $"That Windows starts as {windowsLetter}: from this disk.");
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
