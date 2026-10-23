using System.IO;
using WindowsUtility.Services;
using Xunit;

namespace WindowsUtility.Tests;

/// <summary>A folder backup over several runs: the copy follows the folder, the history keeps what was replaced or removed, and any run can be restored.</summary>
public sealed class FolderBackupHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wu-history-{Guid.NewGuid():N}");
    private string Source => Path.Combine(_root, "Docs");
    private string Target => Path.Combine(_root, "Backup", "Docs");

    public FolderBackupHistoryTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "sub"));
        Write("report.txt", "version 1");
        Write("sub\\notes.txt", "notes");
        Write("old.txt", "to be deleted");
    }

    private void Write(string relative, string text, DateTime? modified = null)
    {
        string path = Path.Combine(Source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, modified ?? DateTime.UtcNow);
    }

    [Fact]
    public void TheCopyFollowsTheFolder_AndTheHistoryKeepsEveryOldVersion()
    {
        DateTime run1 = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        FolderBackupRunner.CopyOutcome first = FolderBackupRunner.Copy(Source, Target, historyDays: 30, run1, CancellationToken.None);
        Assert.Equal((3, 3, 0, 0, 0), (first.Total, first.Copied, first.Unchanged, first.Failed, first.Removed));
        Assert.Equal("version 1", File.ReadAllText(Path.Combine(Target, "report.txt")));

        // Day two: the report is changed by mistake, old.txt is deleted, a new file appears.
        Write("report.txt", "version 2 (the mistake)", DateTime.UtcNow.AddMinutes(1));
        File.Delete(Path.Combine(Source, "old.txt"));
        Write("new.txt", "new");
        DateTime run2 = run1.AddDays(1);
        FolderBackupRunner.CopyOutcome second = FolderBackupRunner.Copy(Source, Target, 30, run2, CancellationToken.None);
        Assert.Equal((3, 2, 1, 0, 2), (second.Total, second.Copied, second.Unchanged, second.Failed, second.Removed));
        Assert.Equal("version 2 (the mistake)", File.ReadAllText(Path.Combine(Target, "report.txt")));
        Assert.False(File.Exists(Path.Combine(Target, "old.txt")));
        string history2 = FolderBackupHistory.RunFolder(Target, run2);
        Assert.Equal("version 1", File.ReadAllText(Path.Combine(history2, "report.txt")));
        Assert.Equal("to be deleted", File.ReadAllText(Path.Combine(history2, "old.txt")));
        Assert.Contains("2 replaced or removed", second.Describe());

        // Day three: nothing changed.
        FolderBackupRunner.CopyOutcome third = FolderBackupRunner.Copy(Source, Target, 30, run2.AddDays(1), CancellationToken.None);
        Assert.Equal((3, 0, 3, 0, 0), (third.Total, third.Copied, third.Unchanged, third.Failed, third.Removed));

        List<BackupVersion> versions = FolderBackupHistory.Versions(Target);
        Assert.Equal(3, versions.Count);
        Assert.Equal(run1, versions[^1].TakenUtc);
        Assert.Equal(3, FolderBackupHistory.Files(versions[^1]).Count);

        // Restoring day one into another folder brings back the report and old.txt, and leaves new.txt out.
        string elsewhere = Path.Combine(_root, "Restored");
        (int restored, int unchanged, List<string> missing) = FolderBackupHistory.Restore(Target, versions[^1], elsewhere, null, CancellationToken.None);
        Assert.Equal((3, 0), (restored, unchanged));
        Assert.Empty(missing);
        Assert.Equal("version 1", File.ReadAllText(Path.Combine(elsewhere, "report.txt")));
        Assert.Equal("to be deleted", File.ReadAllText(Path.Combine(elsewhere, "old.txt")));
        Assert.False(File.Exists(Path.Combine(elsewhere, "new.txt")));

        // Restoring day one over the original folder replaces the mistaken report; unchanged notes are left.
        (restored, unchanged, missing) = FolderBackupHistory.Restore(Target, versions[^1], Source, null, CancellationToken.None);
        Assert.Equal((2, 1), (restored, unchanged));
        Assert.Equal("version 1", File.ReadAllText(Path.Combine(Source, "report.txt")));
    }

    [Fact]
    public void OldHistory_IsPrunedButTheNewestRunStays()
    {
        DateTime run1 = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        FolderBackupRunner.Copy(Source, Target, 30, run1, CancellationToken.None);
        Write("report.txt", "changed", DateTime.UtcNow.AddMinutes(1));
        FolderBackupRunner.Copy(Source, Target, 30, run1.AddDays(1), CancellationToken.None);
        Assert.Equal(2, FolderBackupHistory.Versions(Target).Count);

        // Forty days later: the old runs are gone, only the latest list remains, and the lost version is reported.
        DateTime run3 = run1.AddDays(40);
        FolderBackupRunner.Copy(Source, Target, 30, run3, CancellationToken.None);
        List<BackupVersion> versions = FolderBackupHistory.Versions(Target);
        Assert.Equal(run3, Assert.Single(versions).TakenUtc);
        Assert.False(Directory.Exists(FolderBackupHistory.RunFolder(Target, run1.AddDays(1))));

        // "For ever" keeps everything.
        Write("report.txt", "changed again", DateTime.UtcNow.AddMinutes(2));
        FolderBackupRunner.Copy(Source, Target, 0, run3.AddDays(100), CancellationToken.None);
        Assert.Equal(2, FolderBackupHistory.Versions(Target).Count);
    }

    [Fact]
    public void JunctionsAreNotFollowed_AndSizesRead()
    {
        Assert.Equal("1.5 GB", FolderBackupRunner.FormatBytes(1610612736));
        Assert.Equal("12.0 MB", FolderBackupRunner.FormatBytes(12 * 1024 * 1024));
        Assert.Equal("14 B", FolderBackupRunner.FormatBytes(14));
        FolderBackupRunner.CopyOutcome outcome = FolderBackupRunner.Copy(Source, Target, 30, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal("Folder copied: 3 files, 3 copied, 0 unchanged, 27 B copied.", outcome.Describe()); // "version 1" + "notes" + "to be deleted"
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
