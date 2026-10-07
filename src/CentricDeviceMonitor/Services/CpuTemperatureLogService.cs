using System.IO;
using System.Text.Json;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed class CpuTemperatureLogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private string LogPath => SharedDataPaths.CpuTemperatureLogFilePath;

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public async Task<IReadOnlyList<CpuTemperatureLogEntry>> LoadAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(LogPath))
            {
                return Array.Empty<CpuTemperatureLogEntry>();
            }

            await using FileStream stream = new(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            List<CpuTemperatureLogEntry>? entries =
                await JsonSerializer.DeserializeAsync<List<CpuTemperatureLogEntry>>(stream, JsonOptions);

            if (entries is null)
            {
                return Array.Empty<CpuTemperatureLogEntry>();
            }

            return entries
                .OrderByDescending(entry => entry.StartedAt)
                .ToList();
        }
        catch
        {
            return Array.Empty<CpuTemperatureLogEntry>();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<CpuTemperatureLogEntry> entries)
    {
        await _fileLock.WaitAsync();
        try
        {
            string? directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            List<CpuTemperatureLogEntry> retainedEntries = entries
                .OrderByDescending(entry => entry.StartedAt)
                .Take(1000)
                .ToList();

            string temporaryPath = LogPath + ".tmp";
            await using (FileStream stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, retainedEntries, JsonOptions);
            }

            File.Move(temporaryPath, LogPath, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task ClearCompletedAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(LogPath))
            {
                return;
            }

            List<CpuTemperatureLogEntry> entries;
            await using (FileStream readStream = new(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                entries = await JsonSerializer.DeserializeAsync<List<CpuTemperatureLogEntry>>(readStream, JsonOptions)
                    ?? new List<CpuTemperatureLogEntry>();
            }

            List<CpuTemperatureLogEntry> activeEntries = entries
                .Where(entry => entry.IsActive)
                .OrderByDescending(entry => entry.StartedAt)
                .ToList();

            string temporaryPath = LogPath + ".tmp";
            await using (FileStream writeStream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(writeStream, activeEntries, JsonOptions);
            }

            File.Move(temporaryPath, LogPath, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
