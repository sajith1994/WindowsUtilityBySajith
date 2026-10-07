using System.Globalization;
using System.IO;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed class CpuTemperatureSampleLogService
{
    private const int MaxRowsToLoad = 5000;
    private const long MaxFileSizeBytes = 12 * 1024 * 1024;
    private const int RowsToKeepWhenTrimming = 50000;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private string LogPath => SharedDataPaths.CpuTemperatureSampleLogFilePath;

    public async Task AppendAsync(CpuTemperatureSampleEntry entry)
    {
        await _fileLock.WaitAsync();
        try
        {
            SharedDataPaths.EnsureDirectories();
            bool writeHeader = !File.Exists(LogPath) || new FileInfo(LogPath).Length == 0;

            await using FileStream stream = new(
                LogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            await using StreamWriter writer = new(stream);

            if (writeHeader)
            {
                await writer.WriteLineAsync("RecordedAt,TemperatureCelsius,SensorName,Source");
            }

            string line = string.Join(",",
                EscapeCsv(entry.RecordedAt.ToString("O", CultureInfo.InvariantCulture)),
                EscapeCsv(entry.TemperatureCelsius.ToString("0.0", CultureInfo.InvariantCulture)),
                EscapeCsv(entry.SensorName),
                EscapeCsv(entry.Source));
            await writer.WriteLineAsync(line);
        }
        finally
        {
            _fileLock.Release();
        }

        await TrimIfNeededAsync();
    }

    public async Task<IReadOnlyList<CpuTemperatureSampleEntry>> LoadLatestAsync(int maxRows = 1000)
    {
        maxRows = Math.Clamp(maxRows, 1, MaxRowsToLoad);
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(LogPath))
            {
                return Array.Empty<CpuTemperatureSampleEntry>();
            }

            string[] lines = await File.ReadAllLinesAsync(LogPath);
            if (lines.Length <= 1)
            {
                return Array.Empty<CpuTemperatureSampleEntry>();
            }

            List<CpuTemperatureSampleEntry> entries = new();
            foreach (string line in lines.Skip(1).TakeLast(maxRows))
            {
                string[] fields = ParseCsvLine(line);
                if (fields.Length < 4 ||
                    !DateTime.TryParse(fields[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime recordedAt) ||
                    !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double temperature))
                {
                    continue;
                }

                entries.Add(new CpuTemperatureSampleEntry
                {
                    RecordedAt = recordedAt,
                    TemperatureCelsius = temperature,
                    SensorName = fields[2],
                    Source = fields[3]
                });
            }

            entries.Reverse();
            return entries;
        }
        catch
        {
            return Array.Empty<CpuTemperatureSampleEntry>();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            if (File.Exists(LogPath))
            {
                File.Delete(LogPath);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task TrimIfNeededAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(LogPath) || new FileInfo(LogPath).Length <= MaxFileSizeBytes)
            {
                return;
            }

            string[] lines = await File.ReadAllLinesAsync(LogPath);
            if (lines.Length <= RowsToKeepWhenTrimming + 1)
            {
                return;
            }

            IEnumerable<string> retained = new[] { lines[0] }
                .Concat(lines.Skip(1).TakeLast(RowsToKeepWhenTrimming));
            string temporaryPath = LogPath + ".tmp";
            await File.WriteAllLinesAsync(temporaryPath, retained);
            File.Move(temporaryPath, LogPath, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string[] ParseCsvLine(string line)
    {
        List<string> fields = new();
        System.Text.StringBuilder current = new();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
