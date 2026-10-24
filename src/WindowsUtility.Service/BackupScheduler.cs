using System.Collections.Concurrent;
using System.IO;
using WindowsUtility.Models;
using WindowsUtility.Services;

namespace WindowsUtility.ServiceHost;

/// <summary>
/// Runs the system image backups: on their schedule (a run missed while the PC was off happens when it is on again),
/// on "Run now", and once on "Back up now", one at a time. The schedules live where only SYSTEM and administrators
/// can open them and are changed only through this service, for administrator accounts (the dashboard asks; no
/// administrator prompt needed). The dashboard sees copies without the share passwords.
/// </summary>
internal sealed class BackupScheduler
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(15);
    private readonly BackupScheduleStore _store = BackupScheduleStore.ForImages();
    private readonly BackupRunner _runner = new();
    private readonly ConcurrentQueue<(BackupSchedule Schedule, string Password)> _requested = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _scheduleLock = new();
    private readonly object _stateLock = new();

    /// <summary>The backup running at this moment (one at a time), so "Run now" knows without trusting a saved state.</summary>
    private volatile BackupSchedule? _active;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            BackupScheduleStore.SecureProtectedDirectory();
            MigrateLegacySchedules();
            ClearInterruptedRuns();
            PublishSchedules();
            PublishStates();
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("Image backups: preparing", exception);
        }

        // Give Windows and the disks a moment after start-up before a missed backup begins.
        await WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (_requested.TryDequeue(out (BackupSchedule Schedule, string Password) request))
                {
                    await RunOneAsync(request.Schedule, request.Password, cancellationToken);
                }

                await RunDueAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                ServiceLog.WriteException("Image backups", exception);
            }

            await WaitAsync(CheckEvery, cancellationToken);
        }
    }

    // ---- Requests from the dashboard (administrator accounts) -------------------------------------------------

    /// <summary>Adds or replaces a schedule. The password is kept encrypted for this PC; empty keeps the saved one.</summary>
    public string Save(BackupSchedule schedule, string? password)
    {
        schedule.Kind = BackupKind.SystemImage;
        if (BackupRunner.Validate(schedule) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        lock (_scheduleLock)
        {
            List<BackupSchedule> schedules = _store.LoadSchedules();
            BackupSchedule? existing = schedules.FirstOrDefault(each => each.Id == schedule.Id);
            schedule.ShareSecret = schedule.ShareUser.Length == 0 ? string.Empty
                : password is { Length: > 0 } ? _store.Protect(password)
                : existing?.ShareSecret ?? string.Empty;
            schedules.RemoveAll(each => each.Id == schedule.Id);
            schedules.Add(schedule);
            _store.SaveSchedules(schedules);
            PublishSchedules();
        }

        _wake.Release();
        return $"\"{schedule.Name}\" is saved; it next runs {BackupScheduling.NextRun(schedule, DateTime.Now):ddd yyyy-MM-dd HH:mm}.";
    }

    public string Remove(Guid id)
    {
        lock (_scheduleLock)
        {
            List<BackupSchedule> schedules = _store.LoadSchedules();
            if (schedules.RemoveAll(each => each.Id == id) == 0)
            {
                throw new InvalidOperationException("That backup was not found.");
            }

            _store.SaveSchedules(schedules);
            PublishSchedules();
        }

        lock (_stateLock)
        {
            _store.RemoveState(id);
            PublishStates();
        }

        return "The backup was removed. Backups already made stay where they are.";
    }

    public string RunNow(Guid id)
    {
        BackupSchedule schedule = _store.LoadSchedules().FirstOrDefault(each => each.Id == id) ?? throw new InvalidOperationException("That backup was not found.");
        // What this service is doing decides, not the saved state: a state left "running" by a run the PC stopped
        // mid-way must not refuse every Run now until the next scheduled time.
        if (IsActiveOrQueued(id))
        {
            return $"\"{schedule.Name}\" is already running.";
        }

        Queue(schedule, _store.Unprotect(schedule.ShareSecret));
        return $"\"{schedule.Name}\" starts now; its progress shows in the list.";
    }

    /// <summary>A one-time image backup of the Windows disk to a folder ("Back up now"). Returns its id for the progress.</summary>
    public Guid BackUpOnce(string destination, string shareUser, string password)
    {
        BackupSchedule once = new()
        {
            Name = "Back up now", Kind = BackupKind.SystemImage, WindowsDisk = true, Destination = destination.Trim(),
            ShareUser = shareUser.Trim(), Keep = 30, Frequency = BackupFrequency.Daily
        };
        if (once.Destination.Length == 0)
        {
            throw new InvalidOperationException("Choose where to save the backup.");
        }

        // Only the latest "Back up now" is shown, so earlier finished ones are dropped.
        lock (_stateLock)
        {
            HashSet<Guid> scheduled = _store.LoadSchedules().Select(schedule => schedule.Id).ToHashSet();
            foreach (BackupScheduleState old in _store.LoadStates().Where(state => !state.Running && !scheduled.Contains(state.Id)))
            {
                _store.RemoveState(old.Id);
            }
        }

        Record(new BackupScheduleState { Id = once.Id, Name = once.Name, Running = true, LastRunUtc = DateTime.UtcNow, Stage = "Waiting to start...", LastMessage = "Waiting to start..." });
        Queue(once, password);
        return once.Id;
    }

    private void Queue(BackupSchedule schedule, string password)
    {
        _requested.Enqueue((schedule, password));
        _wake.Release();
    }

    // ---- Running ---------------------------------------------------------------------------------------------

    private async Task RunDueAsync(CancellationToken cancellationToken)
    {
        List<BackupSchedule> schedules = _store.LoadSchedules();
        List<BackupScheduleState> states = _store.LoadStates();
        DateTime created = _store.SchedulesChangedUtc.ToLocalTime();
        foreach (BackupSchedule schedule in schedules.Where(each => each.Kind == BackupKind.SystemImage))
        {
            BackupScheduleState? state = states.FirstOrDefault(each => each.Id == schedule.Id);
            // Backups run one at a time on this loop, so none is running now: a state still "running" was left by a
            // run that was cut short. Saved, so the list stops showing it as running.
            if (state is not null && BackupScheduling.MarkInterrupted(state))
            {
                ServiceLog.Write("Image backups", $"\"{schedule.Name}\" was interrupted before it finished; it runs again at its next time.");
                Record(state);
            }

            if (BackupScheduling.IsDue(schedule, state, DateTime.Now, created))
            {
                await RunOneAsync(schedule, _store.Unprotect(schedule.ShareSecret), cancellationToken);
            }
        }
    }

    private async Task RunOneAsync(BackupSchedule schedule, string password, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        string name = schedule.Name;
        _active = schedule;
        // Progress arrives on pool threads, so a report can come in after the run has ended; once the final state is
        // saved, a late report must not put the run back to "running" for good.
        bool finished = false;
        Record(new BackupScheduleState { Id = schedule.Id, Name = name, LastRunUtc = started, Running = true, Stage = "Starting...", LastMessage = "Running..." });
        ServiceLog.Write("Image backups", $"Starting \"{name}\" to {schedule.Destination} ({BackupScheduling.Describe(schedule)}).");
        DateTime reportedAt = DateTime.MinValue;
        Progress<CloneProgress> progress = new(update =>
        {
            if (update.LogLine is { } line)
            {
                ServiceLog.Write($"Image backups: {name}", line);
                return;
            }

            // The window shows the stage and how far it is; a few writes a minute are enough.
            if (DateTime.UtcNow - reportedAt >= TimeSpan.FromSeconds(3))
            {
                reportedAt = DateTime.UtcNow;
                lock (_stateLock)
                {
                    if (!finished)
                    {
                        Record(new BackupScheduleState { Id = schedule.Id, Name = name, LastRunUtc = started, Running = true, Stage = update.Stage, Fraction = update.Fraction, LastMessage = "Running..." });
                    }
                }
            }
        });

        try
        {
            BackupRunResult result;
            try
            {
                result = await _runner.RunImageAsync(schedule, password, progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Finish(new BackupScheduleState { Id = schedule.Id, Name = name, LastRunUtc = started, LastSucceeded = false, LastMessage = "Stopped: the service was stopping." });
                throw;
            }

            Finish(new BackupScheduleState { Id = schedule.Id, Name = name, LastRunUtc = started, LastSucceeded = result.Succeeded, LastMessage = result.Message, Fraction = result.Succeeded ? 1 : 0 });
            ServiceLog.Write("Image backups", $"\"{name}\" {(result.Succeeded ? "finished" : "failed")}: {result.Message}");
        }
        finally
        {
            _active = null;
        }

        void Finish(BackupScheduleState final)
        {
            lock (_stateLock)
            {
                finished = true;
                Record(final);
            }
        }
    }

    /// <summary>Whether this service is running or about to run the schedule (the saved state is not trusted for this).</summary>
    private bool IsActiveOrQueued(Guid id) =>
        _active?.Id == id || _requested.Any(request => request.Schedule.Id == id);

    /// <summary>
    /// At start, no backup is running, so every state still marked running was left by a run the PC or the service
    /// stopped mid-way (a power cut, a restart). Without this the list showed it running at the same percentage for
    /// ever, and "Run now" answered that it was already running.
    /// </summary>
    private void ClearInterruptedRuns()
    {
        lock (_stateLock)
        {
            foreach (BackupScheduleState state in _store.LoadStates())
            {
                if (BackupScheduling.MarkInterrupted(state))
                {
                    ServiceLog.Write("Image backups", $"\"{state.Name}\" was interrupted before it finished (the PC or the service stopped); it runs again at its next time.");
                    _store.UpdateState(state);
                }
            }
        }
    }

    /// <summary>
    /// Saves a run's state. Progress reports arrive on pool threads, where an exception would end the service, so
    /// writes are taken one at a time and a failed write is only logged.
    /// </summary>
    private void Record(BackupScheduleState state)
    {
        lock (_stateLock)
        {
            try
            {
                _store.UpdateState(state);
                PublishStates();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ServiceLog.WriteException("Image backups: saving progress", exception);
            }
        }
    }

    /// <summary>Copies for the dashboard: the schedules without passwords, and the states.</summary>
    private void PublishSchedules()
    {
        List<BackupSchedule> visible = _store.LoadSchedules();
        visible.ForEach(schedule => schedule.ShareSecret = string.Empty);
        BackupScheduleStore.Save(BackupScheduleStore.PublicImageSchedulesPath, visible);
    }

    private void PublishStates() => BackupScheduleStore.Save(BackupScheduleStore.PublicImageStatesPath, _store.LoadStates());

    /// <summary>
    /// 2.0.88 kept every schedule in one file any user could write. Its image backups are moved into the protected
    /// store only if an administrator (or this service) wrote that file; its folder backups are left for the user's
    /// status icon, which moves them into the user's own profile.
    /// </summary>
    private void MigrateLegacySchedules()
    {
        string legacy = BackupScheduleStore.LegacySchedulesPath;
        if (!File.Exists(legacy))
        {
            return;
        }

        List<BackupSchedule> all = BackupScheduleStore.Load<BackupSchedule>(legacy);
        List<BackupSchedule> images = all.Where(schedule => schedule.Kind == BackupKind.SystemImage).ToList();
        if (images.Count == 0)
        {
            return;
        }

        if (BackupScheduleStore.WrittenByAdministrator(legacy))
        {
            List<BackupSchedule> schedules = _store.LoadSchedules();
            schedules.AddRange(images.Where(image => schedules.All(existing => existing.Id != image.Id))); // same encryption scope
            _store.SaveSchedules(schedules);
            ServiceLog.Write("Image backups", $"Moved {images.Count} image backup schedule(s) from 2.0.88 into the protected store.");
        }
        else
        {
            ServiceLog.Write("Image backups", $"Ignored {images.Count} image backup schedule(s) in a file not written by an administrator.");
        }

        List<BackupSchedule> folders = all.Except(images).ToList();
        if (folders.Count == 0)
        {
            File.Delete(legacy);
            File.Delete(BackupScheduleStore.LegacyStatesPath);
        }
        else
        {
            BackupScheduleStore.Save(legacy, folders);
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await _wake.WaitAsync(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
