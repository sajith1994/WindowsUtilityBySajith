using System.Globalization;
using System.Text;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.ServiceHost;

/// <summary>One reading of the values kept in the history.</summary>
internal readonly record struct HistorySample(double? Cpu, double? Temperature, double? Ram, double? Disk, double? Receive, double? Send);

/// <summary>
/// Records this PC's readings for the history graphs: the service's 2-second readings are averaged per minute and
/// appended to a small CSV file (about 40 bytes a minute), kept for 8 days. Reading it groups the minutes into
/// 5-minute points for a day or 30-minute points for a week; the temperature keeps the highest value of each stretch,
/// everything else the average.
/// </summary>
internal sealed class HistoryRecorder
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(8);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(6);

    private readonly string _path;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private long _minute = -1;
    private Accumulator _current = new();
    private DateTime _prunedAt = DateTime.MinValue;

    /// <param name="path">History file; tests use a temporary file.</param>
    /// <param name="utcNow">Clock; tests move it forward minute by minute.</param>
    public HistoryRecorder(string? path = null, Func<DateTime>? utcNow = null)
    {
        _path = path ?? Path.Combine(SharedDataPaths.LogsDirectory, "history.csv");
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public void Add(BackgroundServiceSnapshot snapshot) => Add(new HistorySample(
        snapshot.CpuUsagePercent,
        snapshot.CpuTemperatureCelsius,
        snapshot.TotalPhysicalMemoryBytes > 0 ? snapshot.RamUsagePercent : null,
        snapshot.DiskActivity.Count > 0 ? snapshot.DiskActivity.Max(disk => disk.ActivePercent) : null,
        snapshot.NetworkActivity.Count > 0 ? snapshot.NetworkActivity.Sum(adapter => adapter.ReceiveBitsPerSec) : null,
        snapshot.NetworkActivity.Count > 0 ? snapshot.NetworkActivity.Sum(adapter => adapter.SendBitsPerSec) : null));

    public void Add(HistorySample sample)
    {
        long minute = new DateTimeOffset(_utcNow()).ToUnixTimeSeconds() / 60;
        lock (_lock)
        {
            if (minute != _minute)
            {
                Flush();
                _minute = minute;
            }

            _current.Add(sample);
        }
    }

    /// <summary>Writes the minute being collected (called when the service stops).</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_minute >= 0 && _current.Count > 0)
            {
                Append(_minute * 60, _current.Average());
            }

            _current = new Accumulator();
        }
    }

    public RemoteHistory Read(int days)
    {
        days = days <= 1 ? 1 : 7;
        int stepMinutes = days == 1 ? 5 : 30;
        DateTime now = _utcNow();
        long stepSeconds = stepMinutes * 60L;
        long end = new DateTimeOffset(now).ToUnixTimeSeconds();
        long start = end - days * 86400L;
        long firstBucket = start / stepSeconds * stepSeconds;

        Dictionary<long, Accumulator> buckets = new();
        long? oldest = null;
        foreach ((long time, HistorySample sample) in ReadLines())
        {
            oldest = oldest is null || time < oldest ? time : oldest;
            if (time < firstBucket || time > end)
            {
                continue;
            }

            long bucket = time / stepSeconds * stepSeconds;
            if (!buckets.TryGetValue(bucket, out Accumulator? accumulator))
            {
                buckets[bucket] = accumulator = new Accumulator();
            }

            accumulator.Add(sample);
        }

        RemoteHistory history = new()
        {
            Days = days,
            StepMinutes = stepMinutes,
            RecordingSinceUtc = oldest is long since ? DateTimeOffset.FromUnixTimeSeconds(since).UtcDateTime : null
        };
        if (buckets.Count == 0)
        {
            return history;
        }

        // Every stretch from the first recorded one to now, empty where the PC was off, so the graph shows the gap.
        for (long bucket = buckets.Keys.Min(); bucket <= end; bucket += stepSeconds)
        {
            HistorySample? value = buckets.TryGetValue(bucket, out Accumulator? accumulator) ? accumulator.Average() : null;
            history.Points.Add(new RemoteHistoryPoint
            {
                TimeUtc = DateTimeOffset.FromUnixTimeSeconds(bucket).UtcDateTime,
                CpuPercent = value?.Cpu,
                TemperatureCelsius = value?.Temperature,
                RamPercent = value?.Ram,
                DiskActivePercent = value?.Disk,
                ReceiveBitsPerSec = value?.Receive,
                SendBitsPerSec = value?.Send
            });
        }

        return history;
    }

    private void Append(long time, HistorySample sample)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (_utcNow() - _prunedAt > PruneEvery)
            {
                Prune();
            }

            File.AppendAllText(_path, Format(time, sample) + "\n", Encoding.ASCII);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ServiceLog.WriteException("History", exception);
        }
    }

    /// <summary>Drops minutes older than 8 days.</summary>
    private void Prune()
    {
        _prunedAt = _utcNow();
        if (!File.Exists(_path))
        {
            return;
        }

        long cutoff = new DateTimeOffset(_utcNow() - Keep).ToUnixTimeSeconds();
        string[] lines = File.ReadAllLines(_path);
        List<string> kept = lines.Where(line => TryParse(line, out long time, out _) && time >= cutoff).ToList();
        if (kept.Count == lines.Length)
        {
            return;
        }

        string temporary = _path + ".tmp";
        File.WriteAllLines(temporary, kept, Encoding.ASCII);
        File.Move(temporary, _path, overwrite: true);
    }

    private IEnumerable<(long Time, HistorySample Sample)> ReadLines()
    {
        string[] lines;
        lock (_lock)
        {
            try
            {
                lines = File.Exists(_path) ? File.ReadAllLines(_path) : Array.Empty<string>();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ServiceLog.WriteException("History read", exception);
                lines = Array.Empty<string>();
            }
        }

        foreach (string line in lines)
        {
            if (TryParse(line, out long time, out HistorySample sample))
            {
                yield return (time, sample);
            }
        }
    }

    internal static string Format(long time, HistorySample sample) => string.Join(",",
        time.ToString(CultureInfo.InvariantCulture),
        Number(sample.Cpu, "0.#"), Number(sample.Temperature, "0.#"), Number(sample.Ram, "0.#"),
        Number(sample.Disk, "0.#"), Number(sample.Receive, "0"), Number(sample.Send, "0"));

    internal static bool TryParse(string line, out long time, out HistorySample sample)
    {
        sample = default;
        string[] parts = line.Split(',');
        if (parts.Length < 7 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out time))
        {
            time = 0;
            return false;
        }

        sample = new HistorySample(Parse(parts[1]), Parse(parts[2]), Parse(parts[3]), Parse(parts[4]), Parse(parts[5]), Parse(parts[6]));
        return true;
    }

    private static string Number(double? value, string format) =>
        value is double number && double.IsFinite(number) ? number.ToString(format, CultureInfo.InvariantCulture) : string.Empty;

    private static double? Parse(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    /// <summary>Running totals of a minute or a longer stretch.</summary>
    private sealed class Accumulator
    {
        private readonly Sum _cpu = new(), _ram = new(), _disk = new(), _receive = new(), _send = new();
        private double? _hottest;

        public int Count { get; private set; }

        public void Add(HistorySample sample)
        {
            Count++;
            _cpu.Add(sample.Cpu);
            _ram.Add(sample.Ram);
            _disk.Add(sample.Disk);
            _receive.Add(sample.Receive);
            _send.Add(sample.Send);
            if (sample.Temperature is double temperature && (_hottest is null || temperature > _hottest))
            {
                _hottest = temperature;
            }
        }

        public HistorySample Average() => new(_cpu.Average, _hottest, _ram.Average, _disk.Average, _receive.Average, _send.Average);

        private sealed class Sum
        {
            private double _total;
            private int _count;

            public double? Average => _count == 0 ? null : _total / _count;

            public void Add(double? value)
            {
                if (value is double number && double.IsFinite(number))
                {
                    _total += number;
                    _count++;
                }
            }
        }
    }
}
