using System.IO;
using System.Text.Json;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed class SystemHealthLogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public async Task<IReadOnlyList<SystemHealthLogEntry>> LoadAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            string path = SharedDataPaths.SystemHealthLogFilePath;
            if (!File.Exists(path))
            {
                return Array.Empty<SystemHealthLogEntry>();
            }

            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            List<SystemHealthLogEntry>? entries =
                await JsonSerializer.DeserializeAsync<List<SystemHealthLogEntry>>(stream, JsonOptions);

            if (entries is null)
            {
                return Array.Empty<SystemHealthLogEntry>();
            }

            return entries
                .OrderByDescending(entry => entry.StartedAt)
                .ToList();
        }
        catch
        {
            return Array.Empty<SystemHealthLogEntry>();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<SystemHealthLogEntry> entries)
    {
        await _fileLock.WaitAsync();
        try
        {
            SharedDataPaths.EnsureDirectories();
            string path = SharedDataPaths.SystemHealthLogFilePath;
            string temporaryPath = path + ".tmp";

            List<SystemHealthLogEntry> retainedEntries = entries
                .OrderByDescending(entry => entry.StartedAt)
                .Take(2000)
                .ToList();

            await using (FileStream stream = new(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.Read))
            {
                await JsonSerializer.SerializeAsync(stream, retainedEntries, JsonOptions);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task ClearCompletedAsync()
    {
        IReadOnlyList<SystemHealthLogEntry> entries = await LoadAsync();
        await SaveAsync(entries.Where(entry => entry.IsActive));
    }
}
