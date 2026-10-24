namespace WindowsUtility.Models;

public enum BackupKind
{
    /// <summary>The partitions of a disk into an image file (Clone, back up and restore).</summary>
    SystemImage,

    /// <summary>One folder copied to another folder, newer files only; nothing is deleted at the destination.</summary>
    Folder
}

public enum BackupFrequency
{
    Daily,
    Weekly,
    Monthly,

    /// <summary>Every <see cref="BackupSchedule.IntervalMinutes"/>, counted from midnight (every 2 hours: 00:00, 02:00, ...).</summary>
    Interval,

    /// <summary>Folder backups only: copies changes shortly after they happen (and everything new when it starts).</summary>
    Live
}

/// <summary>An automatic backup the background service runs on a schedule, set up in the dashboard.</summary>
public sealed class BackupSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public BackupKind Kind { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>System image: the disk Windows runs from (true), or the disk named below.</summary>
    public bool WindowsDisk { get; set; } = true;
    public string DiskName { get; set; } = string.Empty;
    public ulong DiskSize { get; set; }

    /// <summary>System image: how many full backups (each with its incremental ones) to keep; older ones made by this PC are deleted.</summary>
    public int Keep { get; set; } = 3;

    /// <summary>System image: after a full image, the following runs save only what changed since the previous run.</summary>
    public bool Incremental { get; set; } = true;

    /// <summary>System image, incremental: a new full image every this many runs (the chain's length).</summary>
    public int FullEvery { get; set; } = 7;

    /// <summary>Folder backup: the folder copied.</summary>
    public string SourceFolder { get; set; } = string.Empty;

    /// <summary>Where the backup goes: a folder on another partition, an external disk or a network share.</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Sign-in for a network share, when it needs one. The password is encrypted for this PC.</summary>
    public string ShareUser { get; set; } = string.Empty;
    public string ShareSecret { get; set; } = string.Empty;

    public BackupFrequency Frequency { get; set; } = BackupFrequency.Daily;

    /// <summary>Minutes after midnight, local time.</summary>
    public int TimeOfDayMinutes { get; set; } = 2 * 60;

    /// <summary>Weekly: the days it runs on.</summary>
    public List<DayOfWeek> WeekDays { get; set; } = new() { DayOfWeek.Sunday };

    /// <summary>Monthly: 1-28.</summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>Interval: minutes between runs (at least 5; system images at least 60).</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Folder backups: how long replaced and deleted files stay in the backup's history (0 = for ever).</summary>
    public int HistoryDays { get; set; } = 30;
}

/// <summary>How the last run of a schedule went, written by whoever ran it.</summary>
public sealed class BackupScheduleState
{
    public Guid Id { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public bool? LastSucceeded { get; set; }
    public string LastMessage { get; set; } = string.Empty;
    public bool Running { get; set; }

    /// <summary>While running: what it is doing and how far (0-1), for the window to show.</summary>
    public string Stage { get; set; } = string.Empty;
    public double Fraction { get; set; }

    /// <summary>A one-time backup started with "Back up now" (shown until the next one replaces it).</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>When a schedule runs. Pure logic, local time.</summary>
public static class BackupScheduling
{
    /// <summary>What the list shows for a run the PC or the service stopped before it finished.</summary>
    public const string InterruptedMessage = "Interrupted: the PC or the service stopped before it finished. It runs again at its next time; Run now starts it sooner.";

    /// <summary>
    /// Only the background service runs image backups, one at a time, so when it starts no backup can be running.
    /// A state still marked running was left by a run the PC or the service stopped mid-way (a power cut, a
    /// restart). Marks it as such; returns false when the state was not running.
    /// </summary>
    public static bool MarkInterrupted(BackupScheduleState state)
    {
        if (!state.Running)
        {
            return false;
        }

        state.Running = false;
        state.LastSucceeded = false;
        state.Stage = string.Empty;
        state.Fraction = 0;
        state.LastMessage = InterruptedMessage;
        return true;
    }

    /// <summary>The first time the schedule is due strictly after a moment.</summary>
    public static DateTime NextRun(BackupSchedule schedule, DateTime afterLocal)
    {
        TimeSpan time = TimeSpan.FromMinutes(Math.Clamp(schedule.TimeOfDayMinutes, 0, 24 * 60 - 1));
        DateTime day = afterLocal.Date;
        switch (schedule.Frequency)
        {
            case BackupFrequency.Interval:
            case BackupFrequency.Live:
            {
                // Live folders are copied on change; the interval is the catch-up pass that also runs then.
                int minutes = schedule.Frequency == BackupFrequency.Live ? 60 : Math.Clamp(schedule.IntervalMinutes, 5, 24 * 60);
                long steps = (long)Math.Floor((afterLocal - day).TotalMinutes / minutes) + 1;
                DateTime candidate = day.AddMinutes(steps * minutes);
                // The count starts again at midnight, so "every 7 hours" is 00:00, 07:00, 14:00, 21:00 each day.
                return candidate.Date == day ? candidate : day.AddDays(1);
            }
            case BackupFrequency.Weekly when schedule.WeekDays.Count > 0:
                for (int offset = 0; offset <= 7; offset++)
                {
                    DateTime candidate = day.AddDays(offset) + time;
                    if (candidate > afterLocal && schedule.WeekDays.Contains(candidate.DayOfWeek))
                    {
                        return candidate;
                    }
                }

                break;
            case BackupFrequency.Monthly:
                for (int offset = 0; offset <= 2; offset++)
                {
                    DateTime month = new DateTime(day.Year, day.Month, 1).AddMonths(offset);
                    DateTime candidate = month.AddDays(Math.Clamp(schedule.DayOfMonth, 1, DateTime.DaysInMonth(month.Year, month.Month)) - 1) + time;
                    if (candidate > afterLocal)
                    {
                        return candidate;
                    }
                }

                break;
        }

        DateTime today = day + time;
        return today > afterLocal ? today : day.AddDays(1) + time;
    }

    /// <summary>The latest time the schedule was due, at or before a moment; null when it never was.</summary>
    public static DateTime? LastDue(BackupSchedule schedule, DateTime nowLocal)
    {
        DateTime probe = NextRun(schedule, nowLocal.AddDays(schedule.Frequency switch
        {
            BackupFrequency.Monthly => -70,
            BackupFrequency.Interval or BackupFrequency.Live => -2,
            _ => -15
        }));
        if (probe > nowLocal)
        {
            return null;
        }

        while (true)
        {
            DateTime next = NextRun(schedule, probe);
            if (next > nowLocal)
            {
                return probe;
            }

            probe = next;
        }
    }

    /// <summary>
    /// True when a run is owed: the schedule was due since the last run (or has never run and was due since it was
    /// made). A PC that was off at the due time catches up as soon as it is on again.
    /// </summary>
    public static bool IsDue(BackupSchedule schedule, BackupScheduleState? state, DateTime nowLocal, DateTime createdLocal)
    {
        if (!schedule.Enabled || state?.Running == true)
        {
            return false;
        }

        DateTime? due = LastDue(schedule, nowLocal);
        DateTime since = state?.LastRunUtc?.ToLocalTime() ?? createdLocal;
        return due is DateTime moment && moment > since;
    }

    public static string Describe(BackupSchedule schedule)
    {
        string at = $"at {TimeSpan.FromMinutes(schedule.TimeOfDayMinutes):hh\\:mm}";
        int interval = Math.Clamp(schedule.IntervalMinutes, 5, 24 * 60);
        return schedule.Frequency switch
        {
            BackupFrequency.Live => "Live: changes are copied as they happen",
            BackupFrequency.Interval when interval % 60 == 0 => interval == 60 ? "Every hour" : $"Every {interval / 60} hours",
            BackupFrequency.Interval => $"Every {interval} minutes",
            BackupFrequency.Weekly when schedule.WeekDays.Count > 0 =>
                $"Every {string.Join(", ", schedule.WeekDays.Distinct().OrderBy(day => ((int)day + 6) % 7).Select(day => day.ToString()))} {at}",
            BackupFrequency.Monthly => $"Every month on day {schedule.DayOfMonth} {at}",
            _ => $"Every day {at}"
        };
    }
}
