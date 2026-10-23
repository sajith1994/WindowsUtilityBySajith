using System.IO;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WindowsUtility.Models;
using WindowsUtility.Services;
using Xunit;

namespace WindowsUtility.Tests;

public sealed class IncrementalBackupTests : IDisposable
{
    private const ulong MiB = DiskClonePlanner.MiB;
    private const ulong GiB = DiskClonePlanner.GiB;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"wu-inc-{Guid.NewGuid():N}");

    public IncrementalBackupTests() => Directory.CreateDirectory(_folder);

    private static ClonePartition Part(int number, ulong offset, ulong size, string type, bool boot = false, string fileSystem = "NTFS", ulong used = 0, char? letter = null) =>
        new(number, offset, size, type, 0, false, boot, type == DiskClonePlanner.EfiType, letter, fileSystem.Length > 0 ? $@"\\?\Volume{{{number}}}\" : string.Empty,
            fileSystem, used, string.Empty, false, false, $"{{guid-{number}}}");

    private static CloneDisk Disk() => new(0, "SSD", 500 * GiB, "GPT", 512, true, true, "NVMe", new[]
    {
        Part(1, MiB, 100 * MiB, DiskClonePlanner.EfiType, fileSystem: "FAT32"),
        Part(2, 101 * MiB, 16 * MiB, DiskClonePlanner.MsrType, fileSystem: string.Empty),
        Part(3, 117 * MiB, 499 * GiB, DiskClonePlanner.BasicDataType, boot: true, used: 100 * GiB, letter: 'C')
    });

    /// <summary>Writes an image's description (and an empty image file) as a backup run would.</summary>
    private BackupImage Make(string name, string parent = "", string baseName = "", CloneDisk? disk = null)
    {
        disk ??= Disk();
        string path = Path.Combine(_folder, name + BackupImage.ImageExtension);
        File.WriteAllBytes(path, new byte[16]);
        BackupImage image = new(BackupImage.CurrentFormatVersion, DateTime.UtcNow, Environment.MachineName, Path.GetFileName(path), disk.Name, disk.Size, disk.Style, 512, true,
            disk.Partitions.Select(partition => new BackupPartition(partition, 0)).ToList(),
            parent.Length > 0 ? parent + BackupImage.ImageExtension : string.Empty,
            (baseName.Length > 0 ? baseName : parent.Length > 0 ? parent : name) + BackupImage.ImageExtension)
        { ImagePath = path };
        image.Save(BackupImage.DescriptionPathFor(path));
        return image;
    }

    private string Machine => Environment.MachineName;

    [Fact]
    public void AnOldDescription_LoadsAsAFullImage()
    {
        string path = Path.Combine(_folder, "old.vhdx");
        File.WriteAllBytes(path, new byte[16]);
        BackupImage image = new(1, DateTime.UtcNow, "PC", "old.vhdx", "SSD", 10, "GPT", 512, true, new[] { new BackupPartition(Disk().Partitions[2], 1) });
        string json = JsonSerializer.Serialize(image);
        Assert.Contains("ParentImageFileName", json);
        File.WriteAllText(BackupImage.DescriptionPathFor(path), json.Replace("\"ParentImageFileName\":\"\",", "").Replace("\"BaseImageFileName\":\"\"", "\"FormatVersion2\":1"));

        BackupImage loaded = BackupImage.Load(path)!;
        Assert.False(loaded.IsIncremental);
        Assert.Equal("old.vhdx", loaded.ChainBase);
        Assert.True(loaded.ChainComplete());
        Assert.Single(loaded.Chain());
    }

    [Fact]
    public void AChain_IsFollowedToItsFullImage()
    {
        Make($"{Machine}-2026-10-20-0200");
        Make($"{Machine}-2026-10-21-0200-inc", parent: $"{Machine}-2026-10-20-0200");
        BackupImage third = Make($"{Machine}-2026-10-22-0200-inc", parent: $"{Machine}-2026-10-21-0200-inc", baseName: $"{Machine}-2026-10-20-0200");

        Assert.True(third.IsIncremental);
        Assert.Equal(3, third.Chain().Count);
        Assert.True(third.ChainComplete());
        Assert.Equal($"{Machine}-2026-10-20-0200.vhdx", third.ChainBase);

        File.Delete(Path.Combine(_folder, $"{Machine}-2026-10-21-0200-inc.vhdx"));
        Assert.False(BackupImage.Load(third.ImagePath)!.ChainComplete());
    }

    [Fact]
    public void TheParent_IsTheNewestImageUnlessTheChainIsLongOrTheDiskChanged()
    {
        BackupPlan plan = DiskClonePlanner.PlanBackup(Disk(), Disk().Partitions.Where(partition => !partition.IsMsr).ToList(), null, null);
        Assert.Null(BackupRunner.ChooseParent(Array.Empty<BackupImage>(), plan, 7));

        BackupImage full = Make($"{Machine}-2026-10-20-0200");
        Assert.Equal(full.ImageFileName, BackupRunner.ChooseParent(BackupRunner.ImagesIn(_folder), plan, 7)?.ImageFileName);

        BackupImage second = Make($"{Machine}-2026-10-21-0200-inc", parent: $"{Machine}-2026-10-20-0200");
        Assert.Equal(second.ImageFileName, BackupRunner.ChooseParent(BackupRunner.ImagesIn(_folder), plan, 7)?.ImageFileName);
        Assert.Null(BackupRunner.ChooseParent(BackupRunner.ImagesIn(_folder), plan, 2)); // two in the chain already: time for a full one

        // A resized Windows partition means a full backup.
        CloneDisk resized = Disk() with { Partitions = new[] { Disk().Partitions[0], Disk().Partitions[1], Disk().Partitions[2] with { Size = 400 * GiB } } };
        BackupPlan resizedPlan = DiskClonePlanner.PlanBackup(resized, resized.Partitions.Where(partition => !partition.IsMsr).ToList(), null, null);
        Assert.Null(BackupRunner.ChooseParent(BackupRunner.ImagesIn(_folder), resizedPlan, 7));
    }

    [Fact]
    public void OldChains_AreRemovedWhole()
    {
        Make($"{Machine}-2026-10-01-0200");
        Make($"{Machine}-2026-10-02-0200-inc", parent: $"{Machine}-2026-10-01-0200");
        Make($"{Machine}-2026-10-08-0200");
        Make($"{Machine}-2026-10-09-0200-inc", parent: $"{Machine}-2026-10-08-0200");
        BackupImage newest = Make($"{Machine}-2026-10-15-0200");

        int removed = BackupRunner.RemoveOldImages(_folder, newest.ImagePath, keep: 2);

        Assert.Equal(2, removed);
        string[] left = Directory.GetFiles(_folder, "*.vhdx").Select(Path.GetFileName).OrderBy(name => name).ToArray()!;
        Assert.Equal(new[] { $"{Machine}-2026-10-08-0200.vhdx", $"{Machine}-2026-10-09-0200-inc.vhdx", $"{Machine}-2026-10-15-0200.vhdx" }, left);
        Assert.False(File.Exists(Path.Combine(_folder, $"{Machine}-2026-10-01-0200.backup.json")));
    }

    [Fact]
    public void OnlyChangedPieces_AreWritten()
    {
        string sourcePath = Path.Combine(_folder, "source.bin");
        string targetPath = Path.Combine(_folder, "target.bin");
        byte[] data = new byte[10 * 1024 * 1024];
        new Random(21).NextBytes(data);
        File.WriteAllBytes(sourcePath, data);
        byte[] copy = (byte[])data.Clone();
        copy[6 * 1024 * 1024 + 100] ^= 0xFF; // one byte in the seventh 1 MB slice differs
        File.WriteAllBytes(targetPath, copy);

        long processed = 0;
        long written, again;
        using (SafeFileHandle reader = File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (SafeFileHandle writer = File.OpenHandle(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            written = DiskCloneService.CopyRanges(reader, 0, writer, 0, new[] { (0L, (long)data.Length) }, count => processed += count, CancellationToken.None, onlyChanged: true);
            again = DiskCloneService.CopyRanges(reader, 0, writer, 0, new[] { (0L, (long)data.Length) }, _ => { }, CancellationToken.None, onlyChanged: true);
        }

        Assert.Equal(data.Length, processed);
        Assert.Equal(1024 * 1024, written);
        Assert.Equal(0, again);
        Assert.Equal(data, File.ReadAllBytes(targetPath));
    }

    [Fact]
    public void ExcludedStretches_AreCutOutOfTheRanges()
    {
        List<(long Offset, long Length)> ranges = new() { (0, 100), (200, 100), (400, 100) };
        List<(long Offset, long Length)> excluded = new() { (50, 20), (90, 120), (400, 100), (700, 10) };

        Assert.Equal(new[] { (0L, 50L), (70L, 20L), (210L, 90L) }, DiskClonePlanner.Subtract(ranges, excluded));
        Assert.Equal(ranges, DiskClonePlanner.Subtract(ranges, Array.Empty<(long, long)>()));
        Assert.Empty(DiskClonePlanner.Subtract(ranges, new[] { (0L, 1000L) }));
    }

    [Fact]
    public void AnImageWithLargeBlocks_IsNotBuiltOn()
    {
        BackupPlan plan = DiskClonePlanner.PlanBackup(Disk(), Disk().Partitions.Where(partition => !partition.IsMsr).ToList(), null, null);
        BackupImage full = Make($"{Machine}-2026-10-20-0200");
        Assert.Null(Vhdx.BlockSize(full.ImagePath)); // not a VHDX at all: nothing known against it

        WriteVhdx(full.ImagePath, 32 * 1024 * 1024);
        Assert.Equal(32u * 1024 * 1024, Vhdx.BlockSize(full.ImagePath));
        Assert.Null(BackupRunner.ChooseParent(BackupRunner.ImagesIn(_folder), plan, 7));

        WriteVhdx(full.ImagePath, Vhdx.ImageBlockSize);
        Assert.Equal(Vhdx.ImageBlockSize, Vhdx.BlockSize(full.ImagePath));
        Assert.Equal(full.ImageFileName, BackupRunner.ChooseParent(BackupRunner.ImagesIn(_folder), plan, 7)?.ImageFileName);

        WriteVhdx(full.ImagePath, 2 * 1024 * 1024); // Windows' own differencing images have 2 MB blocks: fine to build on
        Assert.Equal(full.ImageFileName, BackupRunner.ChooseParent(BackupRunner.ImagesIn(_folder), plan, 7)?.ImageFileName);
    }

    /// <summary>Just enough of a VHDX file for its block size to be read: the region table and the metadata table.</summary>
    private static void WriteVhdx(string path, uint blockSize)
    {
        const int metadataRegion = 1024 * 1024;
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(metadataRegion + 64 * 1024 + 8);
        stream.Write(System.Text.Encoding.ASCII.GetBytes("vhdxfile"));
        stream.Position = 192 * 1024;
        stream.Write(System.Text.Encoding.ASCII.GetBytes("regi"));
        stream.Position = 192 * 1024 + 8;
        stream.Write(BitConverter.GetBytes(1u));
        stream.Position = 192 * 1024 + 16;
        stream.Write(new Guid("8B7CA206-4790-4B9A-B8FE-575F050F886E").ToByteArray());
        stream.Write(BitConverter.GetBytes((long)metadataRegion));
        stream.Position = metadataRegion;
        stream.Write(System.Text.Encoding.ASCII.GetBytes("metadata"));
        stream.Position = metadataRegion + 10;
        stream.Write(BitConverter.GetBytes((ushort)1));
        stream.Position = metadataRegion + 32;
        stream.Write(new Guid("CAA16737-FA36-4D43-B3B6-33F0AA44E76B").ToByteArray());
        stream.Write(BitConverter.GetBytes(64u * 1024));
        stream.Position = metadataRegion + 64 * 1024;
        stream.Write(BitConverter.GetBytes(blockSize));
    }

    [Fact]
    public void AnIncrementalImage_NeedsRoomOnlyForTheChanges()
    {
        BackupPlan plan = DiskClonePlanner.PlanBackup(Disk(), Disk().Partitions.Where(partition => !partition.IsMsr).ToList(), null, null);
        Assert.Contains("is needed", DiskClonePlanner.SpaceProblem(plan.BytesToCopy, 20 * GiB, incremental: false));
        Assert.Null(DiskClonePlanner.SpaceProblem(plan.BytesToCopy, 20 * GiB, incremental: true));
        Assert.Contains("changes since the last backup", DiskClonePlanner.SpaceProblem(plan.BytesToCopy, 500 * MiB, incremental: true));
        Assert.Null(DiskClonePlanner.SpaceProblem(plan.BytesToCopy, null, incremental: false));
        Assert.Contains("free where the backup would go", DiskClonePlanner.PlanBackup(Disk(), Disk().Partitions.Where(partition => !partition.IsMsr).ToList(), null, 20 * GiB).Problems.Single());
    }

    [Fact]
    public void IncrementalSettings_AreChecked()
    {
        Assert.Null(BackupRunner.Validate(new BackupSchedule { Name = "W", Destination = @"E:\B", Incremental = true, FullEvery = 7 }));
        Assert.Contains("2 to 60", BackupRunner.Validate(new BackupSchedule { Name = "W", Destination = @"E:\B", Incremental = true, FullEvery = 1 }));
        Assert.Null(BackupRunner.Validate(new BackupSchedule { Name = "W", Destination = @"E:\B", Incremental = false, FullEvery = 1 }));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
