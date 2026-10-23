using WindowsUtility.Models;
using WindowsUtility.Services;

namespace WindowsUtility.ServiceHost;

/// <summary>
/// Runs the automatic backups the dashboard set up: once a minute it looks at the schedules, and runs one that is
/// due (or was due while the PC was off), one at a time, writing how it went for the dashboard to show.
/// </summary>
internal sealed class BackupScheduler
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);
    private readonly BackupScheduleStore _store = new();
    private readonly BackupRunner _runner = new();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Give Windows and the disks a moment after start-up before a backup that was missed begins.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                ServiceLog.WriteException("Automatic backups", exception);
            }

            try
            {
                await Task.Delay(CheckEvery, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunDueAsync(CancellationToken cancellationToken)
    {
        List<BackupSchedule> schedules = _store.LoadSchedules();
        if (schedules.Count == 0)
        {
            return;
        }

        List<BackupScheduleState> states = _store.LoadStates();
        DateTime created = _store.SchedulesChangedUtc.ToLocalTime();
        DateTime now = DateTime.Now;
        foreach (BackupSchedule schedule in schedules)
        {
            BackupScheduleState? state = states.FirstOrDefault(each => each.Id == schedule.Id);
            // A state left "running" by a service that stopped mid-way is cleared after it cannot be true any more.
            if (state is { Running: true } && state.LastRunUtc is DateTime started && DateTime.UtcNow - started > TimeSpan.FromHours(12))
            {
                state.Running = false;
            }

            if (!BackupScheduling.IsDue(schedule, state, now, created))
            {
                continue;
            }

            await RunAsync(schedule, cancellationToken);
        }
    }

    private async Task RunAsync(BackupSchedule schedule, CancellationToken cancellationToken)
    {
        BackupScheduleState state = new() { Id = schedule.Id, LastRunUtc = DateTime.UtcNow, Running = true, LastMessage = "Running..." };
        _store.UpdateState(state);
        ServiceLog.Write("Automatic backups", $"Starting \"{schedule.Name}\" ({schedule.Kind}, {BackupScheduling.Describe(schedule)}).");
        Progress<CloneProgress> progress = new(update =>
        {
            if (update.LogLine is { } line)
            {
                ServiceLog.Write($"Automatic backups: {schedule.Name}", line);
            }
        });

        BackupRunResult result;
        try
        {
            result = await _runner.RunAsync(schedule, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _store.UpdateState(new BackupScheduleState { Id = schedule.Id, LastRunUtc = state.LastRunUtc, LastSucceeded = false, LastMessage = "Stopped: the service was stopping." });
            throw;
        }

        _store.UpdateState(new BackupScheduleState { Id = schedule.Id, LastRunUtc = state.LastRunUtc, LastSucceeded = result.Succeeded, LastMessage = result.Message });
        ServiceLog.Write("Automatic backups", $"\"{schedule.Name}\" {(result.Succeeded ? "finished" : "failed")}: {result.Message}");
    }
}
