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
    Monthly
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

    /// <summary>System image: how many backups to keep in the destination; older ones made by this PC are deleted.</summary>
    public int Keep { get; set; } = 3;

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
}

/// <summary>How the last run of a schedule went, written by whoever ran it.</summary>
public sealed class BackupScheduleState
{
    public Guid Id { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public bool? LastSucceeded { get; set; }
    public string LastMessage { get; set; } = string.Empty;
    public bool Running { get; set; }
}

/// <summary>When a schedule runs. Pure logic, local time.</summary>
public static class BackupScheduling
{
    /// <summary>The first time the schedule is due strictly after a moment.</summary>
    public static DateTime NextRun(BackupSchedule schedule, DateTime afterLocal)
    {
        TimeSpan time = TimeSpan.FromMinutes(Math.Clamp(schedule.TimeOfDayMinutes, 0, 24 * 60 - 1));
        DateTime day = afterLocal.Date;
        switch (schedule.Frequency)
        {
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
        DateTime probe = NextRun(schedule, nowLocal.AddDays(schedule.Frequency == BackupFrequency.Monthly ? -70 : -15));
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
        return schedule.Frequency switch
        {
            BackupFrequency.Weekly when schedule.WeekDays.Count > 0 =>
                $"Every {string.Join(", ", schedule.WeekDays.OrderBy(day => ((int)day + 6) % 7).Select(day => day.ToString()))} {at}",
            BackupFrequency.Monthly => $"Every month on day {schedule.DayOfMonth} {at}",
            _ => $"Every day {at}"
        };
    }
}
