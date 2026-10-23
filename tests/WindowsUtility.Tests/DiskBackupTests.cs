using System.IO;
using WindowsUtility.Services;
using Xunit;

namespace WindowsUtility.Tests;

public class DiskBackupTests
{
    private const ulong MiB = DiskClonePlanner.MiB;
    private const ulong GiB = DiskClonePlanner.GiB;

    private static ClonePartition Part(int number, ulong offset, ulong size, string type, bool boot = false, string fileSystem = "NTFS", ulong used = 0,
        char? letter = null, bool encrypted = false, bool locked = false) =>
        new(number, offset, size, type, 0, false, boot, type == DiskClonePlanner.EfiType, letter, fileSystem.Length > 0 ? $@"\\?\Volume{{{number}}}\" : string.Empty,
            fileSystem, used, string.Empty, encrypted, locked, $"{{guid-{number}}}");

    private static CloneDisk WindowsDisk()
    {
        ulong recovery = 750 * MiB;
        ulong cOffset = 117 * MiB;
        ulong cSize = 500 * GiB - MiB - recovery - cOffset;
        return new CloneDisk(0, "Old HDD", 500 * GiB, "GPT", 512, true, true, "SATA", new[]
        {
            Part(1, MiB, 100 * MiB, DiskClonePlanner.EfiType, fileSystem: "FAT32"),
            Part(2, 101 * MiB, 16 * MiB, DiskClonePlanner.MsrType, fileSystem: string.Empty),
            Part(3, cOffset, cSize, DiskClonePlanner.BasicDataType, boot: true, used: 100 * GiB, letter: 'C'),
            Part(4, cOffset + cSize, recovery, DiskClonePlanner.RecoveryType, used: 600 * MiB)
        });
    }

    [Fact]
    public void ABackup_CopiesTheChosenPartitionsAndRecreatesTheReserved()
    {
        CloneDisk disk = WindowsDisk();
        BackupPlan plan = DiskClonePlanner.PlanBackup(disk, disk.Partitions.Where(partition => !partition.IsMsr).ToList(), null, 400 * GiB);

        Assert.True(plan.CanStart, string.Join(" ", plan.Problems));
        Assert.Equal(4, plan.Partitions.Count);
        Assert.Equal(CopyMode.Skip, plan.Partitions[1].Mode);
        Assert.Equal(CopyMode.SnapshotUsed, plan.Partitions[2].Mode);
        Assert.InRange(plan.BytesToCopy, 100 * GiB, 102 * GiB);
        Assert.Equal(500 * GiB, plan.ImageSize);
        Assert.Contains(plan.Notes, note => note.Contains("snapshot"));
    }

    [Fact]
    public void ABackup_RefusesItsOwnPartitionNoRoomAndNothingChosen()
    {
        CloneDisk disk = WindowsDisk();
        ClonePartition windows = disk.Partitions[2];

        Assert.Contains(DiskClonePlanner.PlanBackup(disk, Array.Empty<ClonePartition>(), null, null).Problems, problem => problem.Contains("Tick at least one"));
        Assert.Contains(DiskClonePlanner.PlanBackup(disk, new[] { windows }, windows, 400 * GiB).Problems, problem => problem.Contains("cannot be saved on C:"));
        Assert.Contains(DiskClonePlanner.PlanBackup(disk, new[] { windows }, null, 50 * GiB).Problems, problem => problem.Contains("free where the backup would go"));
        Assert.True(DiskClonePlanner.PlanBackup(disk, new[] { windows }, disk.Partitions[3], 400 * GiB).CanStart); // saved on a partition not backed up
    }

    [Fact]
    public void ABackupWithoutTheBootPartitions_SaysTheRestoreWillNotStart()
    {
        CloneDisk disk = WindowsDisk();
        BackupPlan plan = DiskClonePlanner.PlanBackup(disk, new[] { disk.Partitions[2] }, null, null);
        Assert.True(plan.CanStart);
        Assert.Contains(plan.Notes, note => note.Contains("EFI system") && note.Contains("will not start Windows"));

        BackupPlan dataOnly = DiskClonePlanner.PlanBackup(disk, new[] { disk.Partitions[3] }, null, null);
        Assert.Contains(dataOnly.Notes, note => note.Contains("Without C:"));
    }

    [Fact]
    public void ABackupDescription_RoundTripsAndFindsItsImage()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"wu-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            CloneDisk disk = WindowsDisk();
            string imagePath = Path.Combine(folder, "PC-2026-10-23-0900.vhdx");
            File.WriteAllBytes(imagePath, new byte[512]);
            BackupImage image = new(BackupImage.CurrentFormatVersion, new DateTime(2026, 10, 23, 7, 0, 0, DateTimeKind.Utc), "PC", Path.GetFileName(imagePath),
                disk.Name, disk.Size, disk.Style, disk.LogicalSectorSize, true,
                disk.Partitions.Select(partition => new BackupPartition(partition with { IsEncrypted = false }, 12288)).ToList());
            image.Save(BackupImage.DescriptionPathFor(imagePath));

            BackupImage? loaded = BackupImage.Load(imagePath);
            Assert.NotNull(loaded);
            Assert.Equal(imagePath, loaded!.ImagePath);
            Assert.Equal(4, loaded.Partitions.Count);
            Assert.Equal('C', loaded.Partitions[2].Partition.Letter);
            Assert.True(loaded.HasWindows);
            Assert.Equal(4 * 12288, loaded.BytesCopied);

            CloneDisk restored = loaded.ToDisk();
            Assert.Equal(-1, restored.Number);
            Assert.True(restored.HasWindows);
            Assert.True(DiskClonePlanner.PlanClone(restored, new CloneDisk(1, "New SSD", 1000 * GiB, "RAW", 512, false, false, "NVMe", Array.Empty<ClonePartition>())).CanStart);

            Assert.Null(BackupImage.Load(Path.Combine(folder, "other.vhdx")));
            Assert.Equal(loaded.Title, BackupImage.Load(BackupImage.DescriptionPathFor(imagePath))!.Title);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
