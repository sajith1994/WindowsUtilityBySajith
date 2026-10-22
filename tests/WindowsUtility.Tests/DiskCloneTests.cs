using System.IO;
using System.Text;
using WindowsUtility.Services;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace WindowsUtility.Tests;

public class DiskClonePlannerTests
{
    private const ulong MiB = DiskClonePlanner.MiB;
    private const ulong GiB = DiskClonePlanner.GiB;

    private static ClonePartition Part(int number, ulong offset, ulong size, string type, bool boot = false, string fileSystem = "NTFS", ulong used = 0,
        char? letter = null, bool encrypted = false) =>
        new(number, offset, size, type, 0, false, boot, type == DiskClonePlanner.EfiType, letter, fileSystem.Length > 0 ? $@"\\?\Volume{{{number}}}\" : string.Empty,
            fileSystem, used, string.Empty, encrypted, $"{{guid-{number}}}");

    /// <summary>A typical Windows 11 disk: EFI, MSR, C: and recovery at the end.</summary>
    private static CloneDisk WindowsDisk(ulong size, ulong usedOnC, bool encrypted = false)
    {
        ulong recovery = 750 * MiB;
        ulong cOffset = 117 * MiB;
        ulong cSize = size - MiB - recovery - cOffset;
        return new CloneDisk(0, "Old HDD", size, "GPT", 512, true, true, "SATA", new[]
        {
            Part(1, MiB, 100 * MiB, DiskClonePlanner.EfiType, fileSystem: "FAT32"),
            Part(2, 101 * MiB, 16 * MiB, DiskClonePlanner.MsrType, fileSystem: string.Empty),
            Part(3, cOffset, cSize, DiskClonePlanner.BasicDataType, boot: true, used: usedOnC, letter: 'C', encrypted: encrypted),
            Part(4, cOffset + cSize, recovery, DiskClonePlanner.RecoveryType, used: 600 * MiB)
        });
    }

    private static CloneDisk EmptyDisk(int number, ulong size, int sector = 512) => new(number, "New SSD", size, "RAW", sector, false, false, "NVMe", Array.Empty<ClonePartition>());

    [Fact]
    public void ABiggerSsd_GivesTheExtraSpaceToWindowsAndMovesRecoveryToTheEnd()
    {
        CloneDisk source = WindowsDisk(500 * GiB, 120 * GiB);
        ClonePlan plan = DiskClonePlanner.PlanClone(source, EmptyDisk(1, 1000 * GiB));

        Assert.True(plan.CanStart, string.Join(" ", plan.Problems));
        Assert.Null(plan.ShrinkFirst);
        Assert.Equal(new[] { MiB, 101 * MiB, 117 * MiB }, plan.Partitions.Take(3).Select(partition => partition.Offset));
        PlannedPartition windows = plan.Partitions[2];
        PlannedPartition recovery = plan.Partitions[3];
        Assert.True(windows.Grows);
        Assert.Equal(750 * MiB, recovery.Size);
        Assert.Equal(windows.Offset + windows.Size, recovery.Offset);
        Assert.True(recovery.Offset + recovery.Size <= 1000 * GiB - MiB);
        Assert.Equal(CopyMode.Skip, plan.Partitions[1].Mode);
        Assert.Equal(CopyMode.Raw, plan.Partitions[0].Mode);
        Assert.Equal(CopyMode.SnapshotUsed, windows.Mode);
        Assert.True(plan.TotalBytes < 125 * GiB);
    }

    [Fact]
    public void ASmallerSsd_ShrinksWindowsFirstWhenItsDataFits()
    {
        ClonePlan plan = DiskClonePlanner.PlanClone(WindowsDisk(1000 * GiB, 200 * GiB), EmptyDisk(1, 500 * GiB));
        Assert.True(plan.CanStart, string.Join(" ", plan.Problems));
        Assert.NotNull(plan.ShrinkFirst);
        Assert.True(plan.Partitions[2].Shrinks);
        Assert.True(plan.Partitions[2].Size >= DiskClonePlanner.MinimumSize(plan.Partitions[2].Source));
        Assert.Contains(plan.Notes, note => note.Contains("shrunk first"));
    }

    [Fact]
    public void ASsdTooSmallForTheData_IsRefused()
    {
        ClonePlan plan = DiskClonePlanner.PlanClone(WindowsDisk(1000 * GiB, 480 * GiB), EmptyDisk(1, 500 * GiB));
        Assert.False(plan.CanStart);
        Assert.Contains(plan.Problems, problem => problem.Contains("too small"));
    }

    [Fact]
    public void UnsafeTargets_AreRefused()
    {
        CloneDisk source = WindowsDisk(500 * GiB, 100 * GiB);
        Assert.Contains(DiskClonePlanner.PlanClone(source, source).Problems, problem => problem.Contains("two different disks"));
        Assert.Contains(DiskClonePlanner.PlanClone(source, source with { Number = 5 }).Problems, problem => problem.Contains("running from"));
        Assert.Contains(DiskClonePlanner.PlanClone(source, EmptyDisk(1, 1000 * GiB, sector: 4096)).Problems, problem => problem.Contains("sector sizes"));
        Assert.Contains(DiskClonePlanner.PlanClone(WindowsDisk(500 * GiB, 100 * GiB, encrypted: true), EmptyDisk(1, 1000 * GiB)).Problems,
            problem => problem.Contains("BitLocker"));
    }

    [Fact]
    public void UsedRanges_FollowTheBitmapAndIncludeTheBackupBootSector()
    {
        // 2048 clusters of 4 KB (8 MB): 0-1 used, 3 used (merged with 0-1: the gap is small), 40 used, and 2000 used,
        // which lies inside the last megabyte that is always copied (backup boot sector).
        byte[] bitmap = new byte[256];
        foreach (int cluster in new[] { 0, 1, 3, 40, 2000 })
        {
            bitmap[cluster / 8] |= (byte)(1 << (cluster % 8));
        }

        List<(long Offset, long Length)> ranges = DiskClonePlanner.UsedRanges(bitmap, 2048, 4096, 2048 * 4096L, mergeGap: 4096);
        Assert.Equal(new[] { (0L, 4 * 4096L), (40 * 4096L, 4096L), (7L * 1024 * 1024, 1024L * 1024) }, ranges);
    }

    [Fact]
    public void MountedDevices_ValuesMatchWhatWindowsStores()
    {
        Guid partition = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
        byte[] gpt = DiskClonePlanner.MountedDevicesValue("GPT", partition, 0, 0);
        Assert.Equal("DMIO:ID:", Encoding.ASCII.GetString(gpt, 0, 8));
        Assert.Equal(partition.ToByteArray(), gpt[8..]);

        byte[] mbr = DiskClonePlanner.MountedDevicesValue("MBR", Guid.Empty, 0xA1B2C3D4, 1048576);
        Assert.Equal(12, mbr.Length);
        Assert.Equal(new byte[] { 0xD4, 0xC3, 0xB2, 0xA1 }, mbr[..4]);
        Assert.Equal(1048576UL, BitConverter.ToUInt64(mbr, 4));
    }

    [Fact]
    public void TargetSpaces_ListFreeSpaceAndReplaceablePartitions()
    {
        CloneDisk disk = new(2, "Data", 100 * GiB, "GPT", 512, false, false, "USB", new[]
        {
            Part(1, MiB, 20 * GiB, DiskClonePlanner.BasicDataType, letter: 'E'),
            Part(2, 50 * GiB, 10 * GiB, DiskClonePlanner.BasicDataType, letter: 'F')
        });
        IReadOnlyList<CloneTargetSpace> spaces = DiskClonePlanner.TargetSpaces(disk);
        Assert.Equal(2, spaces.Count(space => space.Replaces is null));
        Assert.Equal(20 * GiB + MiB, spaces[0].Offset);
        Assert.Equal(2, spaces.Count(space => space.Replaces is not null));
    }

    [Fact]
    public void AnEncryptedRecoveryPartition_IsNamedAndExplained()
    {
        ClonePartition dell = Part(5, 400 * GiB, 2 * GiB, DiskClonePlanner.RecoveryType, encrypted: true) with { Label = "DELLSUPPORT" };
        Assert.Equal("Recovery (DELLSUPPORT)", dell.Title);
        Assert.Equal("Recovery", (dell with { Label = "Recovery" }).Title);

        string problem = DiskClonePlanner.EncryptedProblem(dell);
        Assert.Contains("Recovery (DELLSUPPORT) is encrypted with BitLocker", problem);
        Assert.Contains("no drive letter", problem);
        Assert.DoesNotContain("no drive letter", DiskClonePlanner.EncryptedProblem(dell with { Letter = 'D' }));
    }
    [Fact]
    public void ADiskOfTheSameSize_KeepsThePartitionSizes()
    {
        CloneDisk source = WindowsDisk(500 * GiB, 200 * GiB);
        ClonePlan plan = DiskClonePlanner.PlanClone(source, EmptyDisk(1, source.Size + 300 * MiB));

        Assert.True(plan.CanStart, string.Join(" ", plan.Problems));
        Assert.DoesNotContain(plan.Partitions, partition => partition.Grows || partition.Shrinks);
        Assert.DoesNotContain(plan.Notes, note => note.Contains("grows"));
    }
    [Fact]
    public void BootFiles_GoToTheEfiPartitionOfAGptDisk()
    {
        ClonePartition efi = Part(1, MiB, 200 * MiB, DiskClonePlanner.EfiType, fileSystem: "FAT32");
        ClonePartition windows = Part(2, 300 * MiB, 200 * GiB, DiskClonePlanner.BasicDataType, used: 100 * GiB);
        CloneDisk disk = new(1, "New SSD", 500 * GiB, "GPT", 512, false, false, "NVMe", new[] { efi, windows });

        BootSetupPlan plan = DiskClonePlanner.PlanBootSetup(disk, windows);
        Assert.True(plan.CanStart, string.Join(" ", plan.Problems));
        Assert.Equal(efi, plan.Boot);
        Assert.False(plan.SetActive);

        BootSetupPlan noEfi = DiskClonePlanner.PlanBootSetup(disk with { Partitions = new[] { windows } }, windows);
        Assert.Contains(noEfi.Problems, problem => problem.Contains("no EFI system partition"));
    }

    [Fact]
    public void AnMbrDisk_UsesItsActivePartitionOrMakesWindowsActive()
    {
        ClonePartition reserved = Part(1, MiB, 500 * MiB, string.Empty) with { IsActive = true };
        ClonePartition windows = Part(2, 501 * MiB, 200 * GiB, string.Empty, used: 50 * GiB);
        CloneDisk disk = new(1, "New SSD", 500 * GiB, "MBR", 512, false, false, "SATA", new[] { reserved, windows });
        Assert.Equal(reserved, DiskClonePlanner.PlanBootSetup(disk, windows).Boot);

        BootSetupPlan noActive = DiskClonePlanner.PlanBootSetup(disk with { Partitions = new[] { windows } }, windows);
        Assert.Equal(windows, noActive.Boot);
        Assert.True(noActive.SetActive);
    }

    [Fact]
    public void BootSetup_RefusesTheRunningDiskAndPartitionsThatCannotHoldWindows()
    {
        CloneDisk running = WindowsDisk(500 * GiB, 100 * GiB);
        Assert.Contains(DiskClonePlanner.PlanBootSetup(running, running.Partitions[2]).Problems, problem => problem.Contains("running from"));

        ClonePartition fat = Part(2, 300 * MiB, 20 * GiB, DiskClonePlanner.BasicDataType, fileSystem: "FAT32");
        CloneDisk disk = new(1, "USB", 64 * GiB, "GPT", 512, false, false, "USB", new[] { Part(1, MiB, 200 * MiB, DiskClonePlanner.EfiType, fileSystem: "FAT32"), fat });
        Assert.Contains(DiskClonePlanner.PlanBootSetup(disk, fat).Problems, problem => problem.Contains("not NTFS"));
        Assert.False(DiskClonePlanner.PlanBootSetup(disk, null).CanStart);
    }

    [Fact]
    public void APartitionCopy_NeedsEnoughRoomAndCanFillIt()
    {
        ClonePartition data = Part(5, MiB, 10 * GiB, DiskClonePlanner.BasicDataType, used: 4 * GiB, letter: 'D');
        CloneDisk target = EmptyDisk(3, 100 * GiB) with { Style = "GPT" };
        Assert.False(DiskClonePlanner.PlanPartitionCopy(data, 512, target, new CloneTargetSpace(MiB, 5 * GiB, null), false).CanStart);

        ClonePlan fill = DiskClonePlanner.PlanPartitionCopy(data, 512, target, new CloneTargetSpace(MiB, 30 * GiB, null), true);
        Assert.True(fill.CanStart);
        Assert.Equal(30 * GiB, fill.Partitions[0].Size);
        Assert.True(fill.Partitions[0].Grows);
    }
}

public class DiskCopyTests
{
    [Fact]
    public void TheCopy_WritesExactlyTheChosenRanges()
    {
        string sourcePath = Path.GetTempFileName();
        string targetPath = Path.GetTempFileName();
        try
        {
            byte[] data = new byte[12 * 1024 * 1024];
            new Random(3).NextBytes(data);
            File.WriteAllBytes(sourcePath, data);
            File.WriteAllBytes(targetPath, new byte[data.Length]);
            List<(long Offset, long Length)> ranges = new() { (0, 4096 * 3), (5 * 1024 * 1024, 5 * 1024 * 1024 + 8192) };
            long copied = 0;

            using (SafeFileHandle reader = File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read))
            using (SafeFileHandle writer = File.OpenHandle(targetPath, FileMode.Open, FileAccess.ReadWrite))
            {
                DiskCloneService.CopyRanges(reader, 0, writer, 0, ranges, count => copied += count, CancellationToken.None);
                Assert.Null(DiskCloneService.Verify(reader, 0, writer, 0, ranges));
            }

            byte[] result = File.ReadAllBytes(targetPath);
            Assert.Equal(ranges.Sum(range => range.Length), copied);
            Assert.Equal(data[..12288], result[..12288]);
            Assert.All(result[12288..(5 * 1024 * 1024)], value => Assert.Equal(0, value)); // not copied
            int copiedEnd = 10 * 1024 * 1024 + 8192;
            Assert.Equal(data[(5 * 1024 * 1024)..copiedEnd], result[(5 * 1024 * 1024)..copiedEnd]);
            Assert.All(result[copiedEnd..], value => Assert.Equal(0, value)); // past the last range
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(targetPath);
        }
    }

    [Fact]
    public void APartitionWithoutAVolume_IsWrittenAtItsOffsetOnTheDisk()
    {
        string sourcePath = Path.GetTempFileName();
        string diskPath = Path.GetTempFileName();
        try
        {
            byte[] data = new byte[1024 * 1024];
            new Random(9).NextBytes(data);
            File.WriteAllBytes(sourcePath, data);
            File.WriteAllBytes(diskPath, new byte[3 * 1024 * 1024]);
            using (SafeFileHandle reader = File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read))
            using (SafeFileHandle disk = File.OpenHandle(diskPath, FileMode.Open, FileAccess.ReadWrite))
            {
                DiskCloneService.CopyRanges(reader, 0, disk, 1024 * 1024, new[] { (0L, (long)data.Length) }, _ => { }, CancellationToken.None);
                Assert.Null(DiskCloneService.Verify(reader, 0, disk, 1024 * 1024, new[] { (0L, (long)data.Length) }));
            }

            byte[] result = File.ReadAllBytes(diskPath);
            Assert.All(result[..(1024 * 1024)], value => Assert.Equal(0, value));
            Assert.Equal(data, result[(1024 * 1024)..(2 * 1024 * 1024)]);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(diskPath);
        }
    }

    [Fact]
    public void PowerShellErrors_AreReadFromItsClixmlOutput()
    {
        const string clixml = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><S S=\"Error\">New-Partition : Not enough available capacity_x000D__x000A_</S><S S=\"Error\">At line:3 char:1_x000D__x000A_</S></Objs>";
        Assert.Equal("New-Partition : Not enough available capacity", DiskCloneService.CleanPowerShellError(clixml));
        Assert.Equal("Plain error", DiskCloneService.CleanPowerShellError("Plain error\nAt line:1 char:1\n+ ~~~"));
    }

    [Fact]
    public void TheCheck_FindsADifference()
    {
        string sourcePath = Path.GetTempFileName();
        string targetPath = Path.GetTempFileName();
        try
        {
            byte[] data = new byte[2 * 1024 * 1024];
            new Random(5).NextBytes(data);
            File.WriteAllBytes(sourcePath, data);
            byte[] damaged = (byte[])data.Clone();
            for (int index = 0; index < damaged.Length; index += 4096)
            {
                damaged[index] ^= 0xFF;
            }

            File.WriteAllBytes(targetPath, damaged);
            using SafeFileHandle reader = File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read);
            using SafeFileHandle writer = File.OpenHandle(targetPath, FileMode.Open, FileAccess.ReadWrite);
            Assert.NotNull(DiskCloneService.Verify(reader, 0, writer, 0, new[] { (0L, (long)data.Length) }));

            // Differences in files a snapshot does not freeze are expected; anywhere else they name the file.
            static Func<IReadOnlyList<long>, Dictionary<long, List<string>>?> Owned(params string[] files) =>
                offsets => offsets.ToDictionary(offset => offset, _ => files.ToList());
            (long, long)[] all = { (0L, (long)data.Length) };
            Assert.Null(DiskCloneService.Verify(reader, 0, writer, 0, all, Owned(@"\pagefile.sys::$DATA")));
            Assert.Contains(@"in \Users\me\notes.txt", DiskCloneService.Verify(reader, 0, writer, 0, all, Owned(@"\Users\me\notes.txt::$DATA")));
            Assert.NotNull(DiskCloneService.Verify(reader, 0, writer, 0, all, Owned())); // in no file: still a difference
            Assert.NotNull(DiskCloneService.Verify(reader, 0, writer, 0, all, _ => null)); // NTFS could not tell
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(targetPath);
        }
    }

    [Fact]
    public void FilesSnapshotsDoNotFreeze_AreKnown()
    {
        Assert.True(DiskCloneService.ChangesDuringSnapshot(@"\pagefile.sys::$DATA"));
        Assert.True(DiskCloneService.ChangesDuringSnapshot(@"\HIBERFIL.SYS::$DATA"));
        Assert.True(DiskCloneService.ChangesDuringSnapshot(@"\swapfile.sys::$DATA"));
        Assert.True(DiskCloneService.ChangesDuringSnapshot(@"\System Volume Information\{ea9791b8-cdfb-11f1-a5d0-84fdd1598ee9}{3808876b-c176-4e48-b7ae-04046e6cc752}::$DATA"));
        Assert.False(DiskCloneService.ChangesDuringSnapshot(@"\System Volume Information\IndexerVolumeGuid::$DATA"));
        Assert.False(DiskCloneService.ChangesDuringSnapshot(@"\Users\SAJITH\Desktop\Jessica\Downloads\OneDriveSetup.exe::$DATA"));
        Assert.False(DiskCloneService.ChangesDuringSnapshot(@"\Windows\pagefile.sys::$DATA"));
    }

    [Fact]
    public void ACopy_CanBeCancelled()
    {
        string sourcePath = Path.GetTempFileName();
        string targetPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(sourcePath, new byte[8 * 1024 * 1024]);
            File.WriteAllBytes(targetPath, new byte[8 * 1024 * 1024]);
            using SafeFileHandle reader = File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read);
            using SafeFileHandle writer = File.OpenHandle(targetPath, FileMode.Open, FileAccess.ReadWrite);
            using CancellationTokenSource cancel = new();
            Assert.Throws<OperationCanceledException>(() => DiskCloneService.CopyRanges(reader, 0, writer, 0, new[] { (0L, 8L * 1024 * 1024) },
                _ => cancel.Cancel(), cancel.Token));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(targetPath);
        }
    }
}
