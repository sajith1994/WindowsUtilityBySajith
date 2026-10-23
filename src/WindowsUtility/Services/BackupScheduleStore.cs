using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsUtility.Models;

namespace WindowsUtility.Services;

/// <summary>
/// The automatic backups and how their last runs went, in the shared data folder: the dashboard writes the schedules,
/// the background service (and "Run now" in the dashboard) writes the states. Share passwords are encrypted for this
/// PC, so only programs on it (the service runs as SYSTEM) can read them.
/// </summary>
public sealed class BackupScheduleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsUtility backup share");
    private readonly string _schedulesPath;
    private readonly string _statesPath;
    private readonly object _lock = new();

    public BackupScheduleStore(string? schedulesPath = null, string? statesPath = null)
    {
        _schedulesPath = schedulesPath ?? SharedDataPaths.BackupSchedulesFilePath;
        _statesPath = statesPath ?? SharedDataPaths.BackupScheduleStatesFilePath;
    }

    /// <summary>When the schedules file was last written; a schedule made after a run must not count as missed.</summary>
    public DateTime SchedulesChangedUtc => File.Exists(_schedulesPath) ? File.GetLastWriteTimeUtc(_schedulesPath) : DateTime.MinValue;

    public List<BackupSchedule> LoadSchedules() => Load<BackupSchedule>(_schedulesPath);

    public void SaveSchedules(IEnumerable<BackupSchedule> schedules) => Save(_schedulesPath, schedules.ToList());

    public List<BackupScheduleState> LoadStates() => Load<BackupScheduleState>(_statesPath);

    public void UpdateState(BackupScheduleState state)
    {
        lock (_lock)
        {
            List<BackupScheduleState> states = LoadStates();
            states.RemoveAll(existing => existing.Id == state.Id);
            states.Add(state);
            Save(_statesPath, states);
        }
    }

    public void RemoveState(Guid id)
    {
        lock (_lock)
        {
            List<BackupScheduleState> states = LoadStates();
            if (states.RemoveAll(existing => existing.Id == id) > 0)
            {
                Save(_statesPath, states);
            }
        }
    }

    /// <summary>Encrypts a share password for this PC (empty stays empty).</summary>
    public static string Protect(string password) =>
        password.Length == 0 ? string.Empty : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.LocalMachine));

    public static string Unprotect(string secret)
    {
        if (secret.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(secret), Entropy, DataProtectionScope.LocalMachine));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }

    private static List<T> Load<T>(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new List<T>();
            }

            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), JsonOptions) ?? new List<T>();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            ApplicationLogService.WriteException("Backup schedules", exception);
            return new List<T>();
        }
    }

    private static void Save<T>(string path, List<T> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
