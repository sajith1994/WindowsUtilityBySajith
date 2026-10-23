using WindowsUtility.Models;

namespace WindowsUtility.Services;

public sealed record PcIssue(string Text, bool Serious);

/// <summary>
/// What needs attention on a monitored PC now: for the morning summary, and for the phone page (which the
/// background service also writes, so this has nothing from the dashboard's windows).
/// </summary>
public static class PcIssues
{
    /// <summary>Memory use worth mentioning (and alerting about).</summary>
    public const double MemoryThresholdPercent = 95;

    /// <summary>What is wrong with a PC now, most serious first.</summary>
    public static List<PcIssue> For(RemotePcView view)
    {
        List<PcIssue> issues = new();
        if (!view.IsOnline)
        {
            issues.Add(new PcIssue(view.LastSeen is DateTime seen ? $"Offline since {seen:ddd HH:mm}" : $"Not reachable: {view.StateMessage}", true));
            return issues;
        }

        if (view.Stats is not { } stats)
        {
            return issues;
        }

        if (view.IsHot)
        {
            issues.Add(new PcIssue($"Running hot ({view.TemperatureText})", true));
        }

        foreach (RemoteDiskView disk in view.Disks.Where(disk => disk.IsLow))
        {
            issues.Add(new PcIssue($"Drive {disk.Name} nearly full ({disk.Detail})", true));
        }

        if (stats.RamUsagePercent >= MemoryThresholdPercent)
        {
            issues.Add(new PcIssue($"Memory at {stats.RamUsagePercent:0}%", false));
        }

        if (view.ProtectionAtRisk)
        {
            issues.Add(new PcIssue(view.ProtectionText, true));
        }

        if (view.Security is { } security)
        {
            if (RemotePcView.UpdatesStale(security))
            {
                issues.Add(new PcIssue($"No Windows update since {security.LastUpdateInstalledUtc?.ToLocalTime():d MMM}", false));
            }
            else if (security.RestartRequired)
            {
                issues.Add(new PcIssue("Restart needed to finish Windows updates", false));
            }
        }

        if (stats.Problems is { HasSerious: true } problems)
        {
            issues.Add(new PcIssue($"Last 7 days: {problems.Describe()}", true));
        }

        if (view.BatteryWorn)
        {
            issues.Add(new PcIssue($"Battery worn ({stats.BatteryHealthPercent:0}% health)", false));
        }

        if (stats.Recovery is { } recovery && RemotePcView.RecoveryStale(recovery))
        {
            issues.Add(new PcIssue(recovery.SystemProtectionOn == false
                ? "System Protection off (no restore points)"
                : $"No restore point in {RemotePcView.RestorePointStaleDays} days", false));
        }

        return issues;
    }
}
