using System.IO;
using System.Text.Json;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed class DeviceLogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private string LogDirectory => SharedDataPaths.LogsDirectory;

    public async Task AppendAsync(Guid deviceId, DevicePingLogEntry entry)
    {
        entry.SchemaVersion = Math.Max(entry.SchemaVersion, 3);
        await _fileLock.WaitAsync();
        try
        {
            SharedDataPaths.EnsureDirectories();
            string json = JsonSerializer.Serialize(entry, JsonOptions);
            await using FileStream stream = new(
                GetLogPath(deviceId),
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            await using StreamWriter writer = new(stream);
            await writer.WriteLineAsync(json);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task ReplaceAsync(Guid deviceId, IReadOnlyList<DevicePingLogEntry> entries)
    {
        await _fileLock.WaitAsync();
        try
        {
            SharedDataPaths.EnsureDirectories();
            string path = GetLogPath(deviceId);
            string temporaryPath = path + ".tmp";

            await using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            await using (StreamWriter writer = new(stream))
            {
                foreach (DevicePingLogEntry entry in entries.Where(entry => entry.IsIncident).OrderBy(entry => entry.LostAt))
                {
                    entry.SchemaVersion = Math.Max(entry.SchemaVersion, 3);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(entry, JsonOptions));
                }
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<IReadOnlyList<DevicePingLogEntry>> LoadAsync(Guid deviceId, int maximumEntries = 1000)
    {
        await _fileLock.WaitAsync();
        try
        {
            string path = GetLogPath(deviceId);
            if (!File.Exists(path))
            {
                return Array.Empty<DevicePingLogEntry>();
            }

            string text;
            await using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new(stream))
            {
                text = await reader.ReadToEndAsync();
            }

            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            List<DevicePingLogEntry> entries = new();

            foreach (string line in lines.Reverse())
            {
                if (entries.Count >= maximumEntries)
                {
                    break;
                }

                try
                {
                    DevicePingLogEntry? entry = JsonSerializer.Deserialize<DevicePingLogEntry>(line, JsonOptions);
                    // Old 1.x check-by-check records deserialize with SchemaVersion 0 and are intentionally hidden.
                    if (entry?.IsIncident == true)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // Ignore a damaged or legacy line while preserving valid incident entries.
                }
            }

            return entries;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task ClearAsync(Guid deviceId)
    {
        await _fileLock.WaitAsync();
        try
        {
            string path = GetLogPath(deviceId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public Task DeleteAsync(Guid deviceId) => ClearAsync(deviceId);

    private string GetLogPath(Guid deviceId) => Path.Combine(LogDirectory, $"{deviceId:N}.jsonl");
}
