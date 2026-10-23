using System.Collections.Concurrent;
using System.IO;
using WindowsUtility.Models;

namespace WindowsUtility.Services;

/// <summary>
/// Runs the signed-in user's folder backups while they are signed in (the status icon hosts it): every few seconds
/// it looks for one that is due or was missed, and for live ones it watches the folder and copies the changes once
/// it has been quiet for half a minute. Runs as the user, so it needs no administrator rights.
/// </summary>
public sealed class FolderBackupScheduler : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(10);
    private readonly BackupScheduleStore _store;
    private readonly Dictionary<Guid, (FileSystemWatcher Watcher, string Source)> _watchers = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _changedAtUtc = new();
    private readonly HashSet<Guid> _liveStarted = new();
    private readonly CancellationTokenSource _stop = new();

    public FolderBackupScheduler(BackupScheduleStore? store = null) => _store = store ?? BackupScheduleStore.ForUser();

    public void Start() => _ = Task.Run(() => LoopAsync(_stop.Token));

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        BackupScheduleStore.MigrateLegacyFolderSchedules(_store);

        // Let the sign-in settle before a missed backup starts copying.
        if (!await DelayAsync(TimeSpan.FromSeconds(45), cancellationToken))
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(DateTime.Now, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                ApplicationLogService.WriteException("Folder backups", exception);
            }

            if (!await DelayAsync(CheckEvery, cancellationToken))
            {
                return;
            }
        }
    }

    /// <summary>Runs every backup that is due now, one after another.</summary>
    internal async Task RunDueAsync(DateTime nowLocal, CancellationToken cancellationToken)
    {
        List<BackupSchedule> schedules = _store.LoadSchedules().Where(schedule => schedule.Kind == BackupKind.Folder).ToList();
        UpdateWatchers(schedules);
        List<BackupScheduleState> states = _store.LoadStates();
        DateTime created = _store.SchedulesChangedUtc.ToLocalTime();
        foreach (BackupSchedule schedule in schedules.Where(schedule => schedule.Enabled))
        {
            BackupScheduleState? state = states.FirstOrDefault(each => each.Id == schedule.Id);
            if (state is { Running: true } && state.LastRunUtc is DateTime started && DateTime.UtcNow - started > TimeSpan.FromHours(12))
            {
                state.Running = false; // left "running" by a process that ended mid-way
            }

            if (!IsDue(schedule, state, nowLocal, created))
            {
                continue;
            }

            _liveStarted.Add(schedule.Id);
            _changedAtUtc.TryRemove(schedule.Id, out _); // changes made while it copies are picked up next time
            await FolderBackupRunner.RunAsync(schedule, _store, log: null, cancellationToken);
        }
    }

    internal bool IsDue(BackupSchedule schedule, BackupScheduleState? state, DateTime nowLocal, DateTime createdLocal)
    {
        if (state?.Running == true)
        {
            return false;
        }

        if (BackupScheduling.IsDue(schedule, state, nowLocal, createdLocal))
        {
            return true;
        }

        if (schedule.Frequency != BackupFrequency.Live)
        {
            return false;
        }

        // Live: a full pass when it starts (to catch up), then after changes once the folder has been quiet.
        return !_liveStarted.Contains(schedule.Id)
            || _changedAtUtc.TryGetValue(schedule.Id, out DateTime changed) && DateTime.UtcNow - changed >= Quiet;
    }

    /// <summary>Marks a live backup as changed (the watchers call this; tests too).</summary>
    internal void NoteChange(Guid id, DateTime whenUtc) => _changedAtUtc[id] = whenUtc;

    private void UpdateWatchers(List<BackupSchedule> schedules)
    {
        Dictionary<Guid, string> wanted = schedules
            .Where(schedule => schedule.Enabled && schedule.Frequency == BackupFrequency.Live && Directory.Exists(schedule.SourceFolder))
            .ToDictionary(schedule => schedule.Id, schedule => schedule.SourceFolder);
        foreach (Guid id in _watchers.Keys.Where(id => !wanted.TryGetValue(id, out string? source) || !source.Equals(_watchers[id].Source, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _watchers[id].Watcher.Dispose();
            _watchers.Remove(id);
        }

        foreach ((Guid id, string source) in wanted.Where(pair => !_watchers.ContainsKey(pair.Key)))
        {
            try
            {
                FileSystemWatcher watcher = new(source)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024
                };
                void Changed(object? sender, EventArgs e) => NoteChange(id, DateTime.UtcNow);
                watcher.Changed += Changed;
                watcher.Created += Changed;
                watcher.Deleted += Changed;
                watcher.Renamed += Changed;
                watcher.Error += Changed; // too many changes at once: copy anyway
                watcher.EnableRaisingEvents = true;
                _watchers[id] = (watcher, source);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                ApplicationLogService.WriteException($"Folder backups: watching {source}", exception);
            }
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach ((FileSystemWatcher watcher, _) in _watchers.Values)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
        _stop.Dispose();
    }
}
