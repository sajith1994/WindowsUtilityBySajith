using System.Diagnostics;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// Generates a controlled, cancellable CPU workload. The generator deliberately
/// uses one long-running worker per logical processor and applies a short duty
/// cycle when the requested load is below 100 percent.
/// </summary>
public sealed class CpuLoadGenerator : IDisposable
{
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _cancellation;
    private Task[] _workers = [];
    private long _calculationSink;

    public bool IsRunning
    {
        get
        {
            lock (_syncRoot)
            {
                return _cancellation is not null;
            }
        }
    }

    public int WorkerCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _workers.Length;
            }
        }
    }

    public void Start(int loadPercent)
    {
        if (loadPercent is < 25 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(loadPercent),
                "CPU load must be between 25 and 100 percent.");
        }

        lock (_syncRoot)
        {
            if (_cancellation is not null)
            {
                throw new InvalidOperationException("The CPU load generator is already running.");
            }

            CancellationTokenSource cancellation = new();
            int workerCount = Math.Max(1, Environment.ProcessorCount);
            Task[] workers = new Task[workerCount];

            for (int index = 0; index < workerCount; index++)
            {
                int workerIndex = index;
                workers[index] = Task.Factory.StartNew(
                    () => BurnWorker(workerIndex, loadPercent, cancellation.Token),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            _cancellation = cancellation;
            _workers = workers;
        }
    }

    public async Task StopAsync()
    {
        (CancellationTokenSource? cancellation, Task[] workers) = DetachRun();
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal stop path.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void BurnWorker(int workerIndex, int loadPercent, CancellationToken cancellationToken)
    {
        try
        {
            // Keep the WPF UI and temperature-monitoring thread responsive even
            // while every logical processor has an active load worker.
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        }
        catch
        {
            // Continue with the platform default when thread priority is restricted.
        }

        const int dutyCycleMilliseconds = 100;
        long cycleTicks = Math.Max(1, Stopwatch.Frequency * dutyCycleMilliseconds / 1000);
        long activeTicks = Math.Max(1, cycleTicks * loadPercent / 100);
        double value = 0.731 + (workerIndex * 0.017);

        while (!cancellationToken.IsCancellationRequested)
        {
            long cycleStarted = Stopwatch.GetTimestamp();
            do
            {
                for (int iteration = 1; iteration <= 512; iteration++)
                {
                    value = Math.Sin(value + iteration) * Math.Cos(value - iteration) + 1.0000001;
                }
            }
            while (!cancellationToken.IsCancellationRequested &&
                   Stopwatch.GetTimestamp() - cycleStarted < activeTicks);

            // Publish the result once per duty cycle so the JIT cannot remove the
            // calculations without creating unnecessary cross-core contention.
            Interlocked.Exchange(ref _calculationSink, BitConverter.DoubleToInt64Bits(value));

            long elapsedTicks = Stopwatch.GetTimestamp() - cycleStarted;
            long remainingTicks = cycleTicks - elapsedTicks;
            if (remainingTicks <= 0 || cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            int remainingMilliseconds = Math.Max(
                1,
                (int)Math.Ceiling(remainingTicks * 1000.0 / Stopwatch.Frequency));
            cancellationToken.WaitHandle.WaitOne(remainingMilliseconds);
        }
    }

    private (CancellationTokenSource? Cancellation, Task[] Workers) DetachRun()
    {
        lock (_syncRoot)
        {
            CancellationTokenSource? cancellation = _cancellation;
            Task[] workers = _workers;
            _cancellation = null;
            _workers = [];
            return (cancellation, workers);
        }
    }

    public void Dispose()
    {
        (CancellationTokenSource? cancellation, Task[] workers) = DetachRun();
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            Task.WaitAll(workers, TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The workers are expected to finish through cancellation.
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
