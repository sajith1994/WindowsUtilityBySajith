using System.IO;
using System.Security.Cryptography;
using WindowsUtility.Models;
using WindowsUtility.Services;
using Xunit;

namespace WindowsUtility.Tests;

public class BackupScheduleTests
{
    // A Thursday.
    private static readonly DateTime Now = new(2026, 10, 22, 15, 30, 0, DateTimeKind.Local);

    [Fact]
    public void ADailySchedule_RunsAtItsTimeTodayOrTomorrow()
    {
        BackupSchedule schedule = new() { Frequency = BackupFrequency.Daily, TimeOfDayMinutes = 20 * 60 };
        Assert.Equal(new DateTime(2026, 10, 22, 20, 0, 0), BackupScheduling.NextRun(schedule, Now));
        Assert.Equal(new DateTime(2026, 10, 23, 2, 0, 0), BackupScheduling.NextRun(schedule.WithTime(2 * 60), Now));
        Assert.Equal("Every day at 20:00", BackupScheduling.Describe(schedule));
    }

    [Fact]
    public void AWeeklySchedule_PicksTheNextTickedDay()
    {
        BackupSchedule schedule = new() { Frequency = BackupFrequency.Weekly, TimeOfDayMinutes = 60, WeekDays = { DayOfWeek.Monday, DayOfWeek.Sunday } };
        schedule.WeekDays.Clear();
        schedule.WeekDays.AddRange(new[] { DayOfWeek.Monday, DayOfWeek.Sunday });
        Assert.Equal(new DateTime(2026, 10, 25, 1, 0, 0), BackupScheduling.NextRun(schedule, Now)); // Sunday
        Assert.Equal(new DateTime(2026, 10, 26, 1, 0, 0), BackupScheduling.NextRun(schedule, new DateTime(2026, 10, 25, 1, 0, 0))); // then Monday
        Assert.Equal("Every Monday, Sunday at 01:00", BackupScheduling.Describe(schedule));
        Assert.Equal("Every Sunday at 01:00", BackupScheduling.Describe(new BackupSchedule { Frequency = BackupFrequency.Weekly, TimeOfDayMinutes = 60, WeekDays = { DayOfWeek.Sunday } })); // the default Sunday plus Sunday again

        schedule.WeekDays.Clear();
        schedule.WeekDays.Add(DayOfWeek.Thursday);
        Assert.Equal(new DateTime(2026, 10, 29, 1, 0, 0), BackupScheduling.NextRun(schedule, Now)); // today's time is past: next week
    }

    [Fact]
    public void AMonthlySchedule_HandlesShortMonths()
    {
        BackupSchedule schedule = new() { Frequency = BackupFrequency.Monthly, DayOfMonth = 28, TimeOfDayMinutes = 3 * 60 };
        Assert.Equal(new DateTime(2026, 10, 28, 3, 0, 0), BackupScheduling.NextRun(schedule, Now));
        Assert.Equal(new DateTime(2026, 11, 28, 3, 0, 0), BackupScheduling.NextRun(schedule, new DateTime(2026, 10, 28, 3, 0, 0)));
        Assert.Equal(new DateTime(2027, 2, 28, 3, 0, 0), BackupScheduling.NextRun(schedule, new DateTime(2027, 1, 31)));
        Assert.Equal("Every month on day 28 at 03:00", BackupScheduling.Describe(schedule));
    }

    [Fact]
    public void AMissedRun_IsDueUntilItHappens()
    {
        BackupSchedule schedule = new() { Frequency = BackupFrequency.Daily, TimeOfDayMinutes = 2 * 60 };
        DateTime created = new(2026, 10, 20, 12, 0, 0);

        // Made at noon on the 20th: due at 02:00 on the 21st, so still owed at 15:30 on the 22nd (the PC was off).
        Assert.True(BackupScheduling.IsDue(schedule, null, Now, created));
        Assert.Equal(new DateTime(2026, 10, 22, 2, 0, 0), BackupScheduling.LastDue(schedule, Now));

        // Ran this morning: nothing owed until tomorrow.
        BackupScheduleState ran = new() { LastRunUtc = new DateTime(2026, 10, 22, 2, 1, 0).ToUniversalTime() };
        Assert.False(BackupScheduling.IsDue(schedule, ran, Now, created));
        Assert.True(BackupScheduling.IsDue(schedule, ran, Now.AddDays(1), created));

        // Not while running, not when off, and not before it was ever due.
        Assert.False(BackupScheduling.IsDue(schedule, new BackupScheduleState { Running = true }, Now, created));
        Assert.False(BackupScheduling.IsDue(new BackupSchedule { Enabled = false }, null, Now, created));
        Assert.False(BackupScheduling.IsDue(schedule, null, new DateTime(2026, 10, 20, 13, 0, 0), created));
    }

    [Fact]
    public void Schedules_RoundTripWithAnEncryptedPassword()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"wu-sched-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            BackupScheduleStore store = new(Path.Combine(folder, "schedules.json"), Path.Combine(folder, "states.json"), DataProtectionScope.CurrentUser);
            BackupSchedule schedule = new()
            {
                Name = "NAS", Kind = BackupKind.SystemImage, Destination = @"\\nas\backups\pc", ShareUser = "sajith", ShareSecret = store.Protect("s3cret"),
                Frequency = BackupFrequency.Weekly, WeekDays = { DayOfWeek.Friday }
            };
            Assert.NotEqual("s3cret", schedule.ShareSecret);
            store.SaveSchedules(new[] { schedule });

            BackupSchedule loaded = Assert.Single(store.LoadSchedules());
            Assert.Equal(schedule.Id, loaded.Id);
            Assert.Equal(BackupFrequency.Weekly, loaded.Frequency);
            Assert.Equal("s3cret", store.Unprotect(loaded.ShareSecret));
            Assert.Equal(string.Empty, store.Unprotect("not-base64!"));

            store.UpdateState(new BackupScheduleState { Id = schedule.Id, LastRunUtc = DateTime.UtcNow, LastSucceeded = true, LastMessage = "ok" });
            store.UpdateState(new BackupScheduleState { Id = schedule.Id, LastRunUtc = DateTime.UtcNow, LastSucceeded = false, LastMessage = "later" });
            Assert.Equal("later", Assert.Single(store.LoadStates()).LastMessage);
            store.RemoveState(schedule.Id);
            Assert.Empty(store.LoadStates());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

file static class ScheduleExtensions
{
    public static BackupSchedule WithTime(this BackupSchedule schedule, int minutes) => new() { Frequency = schedule.Frequency, TimeOfDayMinutes = minutes, WeekDays = schedule.WeekDays };
}
