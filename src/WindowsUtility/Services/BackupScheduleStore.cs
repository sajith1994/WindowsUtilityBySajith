using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsUtility.Models;

namespace WindowsUtility.Services;

/// <summary>
/// Automatic backups and how their last runs went. Two stores, so nobody can make the service copy what they could
/// not read themselves:
/// <list type="bullet">
/// <item>Folder backups belong to the signed-in user: kept in their own profile, run as them (by the status icon and
/// the dashboard), the share password encrypted for their account.</item>
/// <item>System image backups need the service (reading a whole disk needs administrator rights): only the service
/// writes them, for administrator accounts, into a folder only SYSTEM and administrators can open; it also writes a
/// copy without passwords that the dashboard shows.</item>
/// </list>
/// </summary>
public sealed class BackupScheduleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WindowsUtility backup share");
    private readonly string _schedulesPath;
    private readonly string _statesPath;
    private readonly DataProtectionScope _scope;
    private readonly object _lock = new();

    public BackupScheduleStore(string schedulesPath, string statesPath, DataProtectionScope scope)
    {
        _schedulesPath = schedulesPath;
        _statesPath = statesPath;
        _scope = scope;
    }

    /// <summary>This user's folder backups.</summary>
    public static BackupScheduleStore ForUser() => new(
        Path.Combine(SharedDataPaths.UserDirectory, "folder-backups.json"),
        Path.Combine(SharedDataPaths.UserDirectory, "folder-backup-states.json"),
        DataProtectionScope.CurrentUser);

    /// <summary>System image backups; only the service (SYSTEM) and administrators can open the schedules.</summary>
    public static BackupScheduleStore ForImages() => new(
        Path.Combine(ProtectedDirectory, "image-backups.json"),
        Path.Combine(ProtectedDirectory, "image-backup-states.json"),
        DataProtectionScope.LocalMachine);

    /// <summary>The image backups as the dashboard may see them: everything but the share passwords.</summary>
    public static string PublicImageSchedulesPath => Path.Combine(SharedDataPaths.ServiceDirectory, "image-backups.public.json");

    /// <summary>How the image backups went, copied for the dashboard (the service schedules from its own copy).</summary>
    public static string PublicImageStatesPath => Path.Combine(SharedDataPaths.ServiceDirectory, "image-backup-states.public.json");

    public static List<BackupSchedule> LoadPublicImageSchedules() => Load<BackupSchedule>(PublicImageSchedulesPath);

    public static List<BackupScheduleState> LoadPublicImageStates() => Load<BackupScheduleState>(PublicImageStatesPath);

    public static string ProtectedDirectory => Path.Combine(SharedDataPaths.ServiceDirectory, "protected");

    /// <summary>The single file 2.0.88 kept both kinds in, writable by every user.</summary>
    public static string LegacySchedulesPath => Path.Combine(SharedDataPaths.RootDirectory, "backup-schedules.json");

    public static string LegacyStatesPath => Path.Combine(SharedDataPaths.ServiceDirectory, "backup-schedule-states.json");

    /// <summary>When the schedules file was last written; a schedule made after a due time does not count as missed.</summary>
    public DateTime SchedulesChangedUtc => File.Exists(_schedulesPath) ? File.GetLastWriteTimeUtc(_schedulesPath) : DateTime.MinValue;

    public List<BackupSchedule> LoadSchedules() => Load<BackupSchedule>(_schedulesPath);

    public void SaveSchedules(IEnumerable<BackupSchedule> schedules) => Save(_schedulesPath, schedules.ToList());

    public List<BackupScheduleState> LoadStates() => Load<BackupScheduleState>(_statesPath);

    public BackupScheduleState? LoadState(Guid id) => LoadStates().FirstOrDefault(state => state.Id == id);

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

    /// <summary>Encrypts a share password for this store's owner (the user, or this PC for the service).</summary>
    public string Protect(string password) =>
        password.Length == 0 ? string.Empty : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, _scope));

    public string Unprotect(string secret)
    {
        if (secret.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(secret), Entropy, _scope));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Moves 2.0.88's folder backups (in the shared file every user could write) into this user's own store, with the
    /// share password encrypted for the user. The service has already taken the image backups out.
    /// </summary>
    public static void MigrateLegacyFolderSchedules(BackupScheduleStore userStore)
    {
        string legacy = LegacySchedulesPath;
        try
        {
            List<BackupSchedule> all = Load<BackupSchedule>(legacy);
            List<BackupSchedule> folders = all.Where(schedule => schedule.Kind == BackupKind.Folder).ToList();
            if (folders.Count == 0)
            {
                return;
            }

            BackupScheduleStore machine = new(legacy, LegacyStatesPath, DataProtectionScope.LocalMachine);
            List<BackupSchedule> mine = userStore.LoadSchedules();
            foreach (BackupSchedule folder in folders.Where(folder => mine.All(existing => existing.Id != folder.Id)))
            {
                folder.ShareSecret = userStore.Protect(machine.Unprotect(folder.ShareSecret));
                mine.Add(folder);
            }

            userStore.SaveSchedules(mine);

            // Their last runs come along, so the list does not say "Never" and a run is not repeated.
            HashSet<Guid> moved = folders.Select(folder => folder.Id).ToHashSet();
            List<BackupScheduleState> states = userStore.LoadStates();
            foreach (BackupScheduleState state in machine.LoadStates().Where(state => moved.Contains(state.Id) && states.All(existing => existing.Id != state.Id)))
            {
                userStore.UpdateState(state);
            }

            List<BackupSchedule> rest = all.Except(folders).ToList();
            if (rest.Count == 0)
            {
                File.Delete(legacy);
            }
            else
            {
                Save(legacy, rest);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            ApplicationLogService.WriteException("Backup schedules: moving from 2.0.88", exception);
        }
    }

    /// <summary>
    /// Creates the folder for the image schedules so only SYSTEM and administrators can open it (the service calls
    /// this at start; its parent lets every user write, which is why the schedules cannot live there).
    /// </summary>
    public static void SecureProtectedDirectory()
    {
        DirectoryInfo directory = Directory.CreateDirectory(ProtectedDirectory);
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        foreach (WellKnownSidType sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        directory.SetAccessControl(security);
    }

    /// <summary>True when only SYSTEM or administrators could have written the file (its owner is one of them).</summary>
    public static bool WrittenByAdministrator(string path)
    {
        try
        {
            SecurityIdentifier? owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SystemException)
        {
            return false;
        }
    }

    internal static List<T> Load<T>(string path)
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

    internal static void Save<T>(string path, List<T> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
