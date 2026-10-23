extern alias service;
using System.IO;
using System.Security.Cryptography;
using WindowsUtility.Models;
using WindowsUtility.Services;
using Xunit;
using PhoneViewPoller = service::WindowsUtility.ServiceHost.PhoneViewPoller;

namespace WindowsUtility.Tests;

public class BackupFeatureTests
{
    // A Thursday afternoon.
    private static readonly DateTime Now = new(2026, 10, 22, 15, 30, 0, DateTimeKind.Local);

    [Fact]
    public void AnInterval_CountsFromMidnight()
    {
        BackupSchedule twoHours = new() { Frequency = BackupFrequency.Interval, IntervalMinutes = 120 };
        Assert.Equal(new DateTime(2026, 10, 22, 16, 0, 0), BackupScheduling.NextRun(twoHours, Now));
        Assert.Equal("Every 2 hours", BackupScheduling.Describe(twoHours));

        BackupSchedule fifteen = new() { Frequency = BackupFrequency.Interval, IntervalMinutes = 15 };
        Assert.Equal(new DateTime(2026, 10, 22, 15, 45, 0), BackupScheduling.NextRun(fifteen, Now));
        Assert.Equal("Every 15 minutes", BackupScheduling.Describe(fifteen));
        Assert.Equal("Every hour", BackupScheduling.Describe(new BackupSchedule { Frequency = BackupFrequency.Interval, IntervalMinutes = 60 }));

        // Seven hours does not divide a day: 00:00, 07:00, 14:00, 21:00, then midnight again.
        BackupSchedule seven = new() { Frequency = BackupFrequency.Interval, IntervalMinutes = 7 * 60 };
        Assert.Equal(new DateTime(2026, 10, 22, 21, 0, 0), BackupScheduling.NextRun(seven, Now));
        Assert.Equal(new DateTime(2026, 10, 23, 0, 0, 0), BackupScheduling.NextRun(seven, new DateTime(2026, 10, 22, 21, 0, 0)));
    }

    [Fact]
    public void AnInterval_IsDueOncePerStepSinceTheLastRun()
    {
        BackupSchedule hourly = new() { Frequency = BackupFrequency.Interval, IntervalMinutes = 60 };
        DateTime created = Now.AddDays(-1);
        Assert.True(BackupScheduling.IsDue(hourly, new BackupScheduleState { LastRunUtc = new DateTime(2026, 10, 22, 14, 5, 0).ToUniversalTime() }, Now, created));
        Assert.False(BackupScheduling.IsDue(hourly, new BackupScheduleState { LastRunUtc = new DateTime(2026, 10, 22, 15, 1, 0).ToUniversalTime() }, Now, created));
        Assert.Equal(new DateTime(2026, 10, 22, 15, 0, 0), BackupScheduling.LastDue(hourly, Now));
    }

    [Fact]
    public void SystemImages_AreNeverLiveOrMoreThanHourly()
    {
        BackupSchedule image = new() { Kind = BackupKind.SystemImage, Name = "Windows", Destination = @"E:\Backups" };
        Assert.Null(BackupRunner.Validate(image));
        Assert.Contains("live", BackupRunner.Validate(new BackupSchedule { Name = "W", Destination = @"E:\B", Frequency = BackupFrequency.Live }));
        Assert.Contains("once an hour", BackupRunner.Validate(new BackupSchedule { Name = "W", Destination = @"E:\B", Frequency = BackupFrequency.Interval, IntervalMinutes = 30 }));
        Assert.Contains("Keep between", BackupRunner.Validate(new BackupSchedule { Name = "W", Destination = @"E:\B", Keep = 0 }));
        Assert.Contains("name", BackupRunner.Validate(new BackupSchedule { Destination = @"E:\B" }));
    }

    [Fact]
    public void AFolderBackup_CannotGoInsideItself()
    {
        string source = Path.GetTempPath().TrimEnd('\\');
        BackupSchedule inside = new() { Kind = BackupKind.Folder, Name = "Temp", SourceFolder = source, Destination = Path.Combine(source, "copy") };
        Assert.Contains("inside the folder", FolderBackupRunner.Validate(inside));
        Assert.Contains("not found", FolderBackupRunner.Validate(new BackupSchedule { Name = "X", SourceFolder = @"Z:\no\such\folder", Destination = @"E:\B" }));
        Assert.Null(FolderBackupRunner.Validate(new BackupSchedule { Name = "Temp", SourceFolder = source, Destination = @"E:\Backups" }));
        Assert.Equal(@"E:\Backups\D", FolderBackupRunner.TargetFolder(new BackupSchedule { SourceFolder = @"D:\", Destination = @"E:\Backups" }));
        Assert.Equal(@"E:\Backups\Documents", FolderBackupRunner.TargetFolder(new BackupSchedule { SourceFolder = @"C:\Users\me\Documents\", Destination = @"E:\Backups" }));
    }

    [Fact]
    public async Task ALiveFolder_IsCopiedAtOnceThenAfterChangesHaveSettled()
    {
        string root = Path.Combine(Path.GetTempPath(), $"wu-live-{Guid.NewGuid():N}");
        string source = Path.Combine(root, "Work");
        string destination = Path.Combine(root, "Backup");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "first");
        File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "second");
        try
        {
            BackupScheduleStore store = new(Path.Combine(root, "schedules.json"), Path.Combine(root, "states.json"), DataProtectionScope.CurrentUser);
            BackupSchedule live = new() { Kind = BackupKind.Folder, Name = "Work", SourceFolder = source, Destination = destination, Frequency = BackupFrequency.Live };
            store.SaveSchedules(new[] { live });
            using FolderBackupScheduler scheduler = new(store);

            // The first pass copies everything.
            await scheduler.RunDueAsync(DateTime.Now, CancellationToken.None);
            Assert.Equal("second", File.ReadAllText(Path.Combine(destination, "Work", "sub", "b.txt")));
            BackupScheduleState state = Assert.Single(store.LoadStates());
            Assert.True(state.LastSucceeded, state.LastMessage);
            Assert.False(scheduler.IsDue(live, state, DateTime.Now, DateTime.Now.AddMinutes(-1)));

            // The folder is now watched: a change is noticed, but copied only once it has been quiet for half a minute.
            File.WriteAllText(Path.Combine(source, "c.txt"), "third");
            await Task.Delay(1000);
            Assert.False(scheduler.IsDue(live, state, DateTime.Now, DateTime.Now.AddMinutes(-1)));
            scheduler.NoteChange(live.Id, DateTime.UtcNow.AddSeconds(-31)); // as if half a minute had passed
            await scheduler.RunDueAsync(DateTime.Now, CancellationToken.None);
            Assert.Equal("third", File.ReadAllText(Path.Combine(destination, "Work", "c.txt")));

            // Deleting from the folder removes it from the copy, but keeps it in the history for a restore.
            File.Delete(Path.Combine(source, "a.txt"));
            await Task.Delay(1000);
            scheduler.NoteChange(live.Id, DateTime.UtcNow.AddSeconds(-31));
            await scheduler.RunDueAsync(DateTime.Now, CancellationToken.None);
            string target = Path.Combine(destination, "Work");
            Assert.False(File.Exists(Path.Combine(target, "a.txt")));
            Assert.Contains(Directory.GetFiles(FolderBackupHistory.HistoryFolder(target), "a.txt", SearchOption.AllDirectories), path => File.ReadAllText(path) == "first");
            Assert.True(FolderBackupHistory.Versions(target).Count >= 2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ThePhonePage_IsLeftToTheDashboardWhileItWritesIt()
    {
        DateTime now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        DateTime byService = now.AddSeconds(-40);
        Assert.True(PhoneViewPoller.DashboardIsWriting(now.AddSeconds(-30), byService, now)); // the dashboard wrote after the service
        Assert.False(PhoneViewPoller.DashboardIsWriting(byService, byService, now)); // only the service has written
        Assert.False(PhoneViewPoller.DashboardIsWriting(now.AddMinutes(-5), DateTime.MinValue, now)); // the dashboard stopped (restart)
        Assert.False(PhoneViewPoller.DashboardIsWriting(DateTime.MinValue, DateTime.MinValue, now)); // never written
    }
}
