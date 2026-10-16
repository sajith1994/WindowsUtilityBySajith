using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed record RemoteAlert(DateTime At, string PcName, string Title, string Message, bool IsRecovery);

/// <summary>
/// Turns poll results into notifications. Each problem is announced once when it starts and re-armed when it clears.
/// Overheating, memory and offline must last a minute so a single spike or dropped poll does not notify.
/// </summary>
public sealed class RemoteAlertTracker
{
    public const double MemoryThresholdPercent = 95;
    public const double DiskNearlyFullFreeFraction = 0.10;
    public const double DiskFullFreeFraction = 0.02;
    public const long DiskFullFreeBytes = 1L << 30;
    private static readonly TimeSpan SustainedFor = TimeSpan.FromMinutes(1);

    private readonly Dictionary<Guid, PcState> _states = new();

    public IReadOnlyList<RemoteAlert> Evaluate(RemotePcEntry pc, string name, RemoteStatsResult result, AppSettings settings, DateTime now)
    {
        PcState state = _states.TryGetValue(pc.Id, out PcState? existing) ? existing : _states[pc.Id] = new PcState();
        List<RemoteAlert> alerts = new();
        HashSet<string> active = new();

        void Raise(string key, bool condition, bool sustained, string title, string message)
        {
            if (!condition)
            {
                state.Since.Remove(key);
                state.Notified.Remove(key);
                return;
            }

            active.Add(key);
            DateTime since = state.Since.TryGetValue(key, out DateTime started) ? started : state.Since[key] = now;
            if (!state.Notified.Contains(key) && (!sustained || now - since >= SustainedFor))
            {
                state.Notified.Add(key);
                alerts.Add(new RemoteAlert(now, name, title, message, false));
            }
        }

        if (result.State == RemoteStatsState.Online && result.Stats is { } stats)
        {
            if (state.Notified.Contains("offline"))
            {
                alerts.Add(new RemoteAlert(now, name, $"{name} is back online", $"{name} answered again after being unreachable.", true));
            }

            state.SeenOnline = true;
            Raise("offline", false, true, string.Empty, string.Empty);

            bool hot = stats.CpuTemperatureIsCpuSpecific && stats.CpuTemperatureCelsius is double temperature
                && temperature >= stats.CpuTemperatureAlertThresholdCelsius;
            Raise("hot", settings.RemoteAlertOverheat && hot, true,
                $"{name} is overheating",
                $"CPU at {stats.CpuTemperatureCelsius:0} °C (alert at {stats.CpuTemperatureAlertThresholdCelsius:0} °C) for over a minute. CPU load {stats.CpuUsagePercent:0}%.");

            Raise("ram", settings.RemoteAlertMemory && stats.RamUsagePercent >= MemoryThresholdPercent, true,
                $"{name} is running out of memory",
                $"Memory at {stats.RamUsagePercent:0}% for over a minute ({RemotePcView.FormatGb(stats.TotalMemoryBytes - Math.Min(stats.AvailableMemoryBytes, stats.TotalMemoryBytes))} of {RemotePcView.FormatGb(stats.TotalMemoryBytes)} used).");

            foreach (RemoteDiskInfo disk in stats.Disks.Where(disk => disk.TotalBytes > 0))
            {
                bool full = disk.FreeBytes < disk.TotalBytes * DiskFullFreeFraction || disk.FreeBytes < DiskFullFreeBytes;
                bool nearlyFull = !full && disk.FreeBytes < disk.TotalBytes * DiskNearlyFullFreeFraction;
                string free = $"{RemotePcView.FormatGb(disk.FreeBytes)} free of {RemotePcView.FormatGb(disk.TotalBytes)}";
                string nearlyKey = $"disk:{disk.Name}";
                Raise($"diskfull:{disk.Name}", settings.RemoteAlertDisk && full, false,
                    $"{name}: drive {disk.Name} is full", $"Only {free}. Programs and Windows updates may fail.");
                if (settings.RemoteAlertDisk && full)
                {
                    // Full already covers nearly full: keep it quiet, and do not announce it again if space is freed
                    // only down to nearly full.
                    active.Add(nearlyKey);
                    state.Since.TryAdd(nearlyKey, now);
                    state.Notified.Add(nearlyKey);
                }
                else
                {
                    Raise(nearlyKey, settings.RemoteAlertDisk && nearlyFull, false,
                        $"{name}: drive {disk.Name} is nearly full", $"{free} ({100.0 * (disk.TotalBytes - disk.FreeBytes) / disk.TotalBytes:0}% used).");
                }
            }
        }
        else if (result.State is RemoteStatsState.Unreachable or RemoteStatsState.NotSharing)
        {
            // Only PCs seen online since this dashboard started: a PC that was already off is not news.
            Raise("offline", settings.RemoteAlertOffline && state.SeenOnline, true,
                $"{name} is offline",
                $"{name} has not answered for over a minute. {result.Message}");
            // Other problems stay as they were: a full drive is not news again when the PC comes back.
            return alerts;
        }
        else
        {
            // Wrong code or an unreadable answer: keep the last known problems, raise nothing new.
            return alerts;
        }

        KeepOnly(state, active);
        return alerts;
    }

    public void Forget(Guid pcId) => _states.Remove(pcId);

    /// <summary>Problems that are not reported in this result (e.g. a drive that was removed) are cleared.</summary>
    private static void KeepOnly(PcState state, HashSet<string> active)
    {
        foreach (string key in state.Since.Keys.Where(key => !active.Contains(key)).ToList())
        {
            state.Since.Remove(key);
        }

        state.Notified.RemoveWhere(key => !active.Contains(key));
    }

    private sealed class PcState
    {
        public Dictionary<string, DateTime> Since { get; } = new();
        public HashSet<string> Notified { get; } = new();
        public bool SeenOnline { get; set; }
    }
}
