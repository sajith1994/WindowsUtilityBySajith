using System.IO;
using System.Text.Json;

namespace CentricDeviceMonitor.Services;

/// <summary>Notifications about monitored PCs from the last 8 days, kept for the daily summary (survives a restart).</summary>
public sealed class RemoteAlertLog
{
    private static readonly TimeSpan Keep = TimeSpan.FromDays(8);
    private readonly string _path;
    private readonly object _lock = new();
    private List<RemoteAlert>? _alerts;

    public RemoteAlertLog(string? path = null)
    {
        _path = path ?? Path.Combine(SharedDataPaths.LogsDirectory, "network-pc-alerts.json");
    }

    public void Add(RemoteAlert alert)
    {
        lock (_lock)
        {
            List<RemoteAlert> alerts = Load();
            alerts.Add(alert);
            alerts.RemoveAll(old => DateTime.Now - old.At > Keep);
            try
            {
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(_path, JsonSerializer.Serialize(alerts));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ApplicationLogService.WriteException("Network PCs alert log", exception);
            }
        }
    }

    public IReadOnlyList<RemoteAlert> Since(DateTime since)
    {
        lock (_lock)
        {
            return Load().Where(alert => alert.At >= since).OrderBy(alert => alert.At).ToList();
        }
    }

    private List<RemoteAlert> Load()
    {
        if (_alerts is not null)
        {
            return _alerts;
        }

        try
        {
            _alerts = File.Exists(_path) ? JsonSerializer.Deserialize<List<RemoteAlert>>(File.ReadAllText(_path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            ApplicationLogService.WriteException("Network PCs alert log", exception);
        }

        return _alerts ??= new List<RemoteAlert>();
    }
}
