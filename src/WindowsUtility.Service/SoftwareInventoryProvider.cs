using System.Globalization;
using System.Security.Principal;
using System.Text.RegularExpressions;
using WindowsUtility.Models;
using Microsoft.Win32;

namespace WindowsUtility.ServiceHost;

/// <summary>
/// Programs installed on this PC, as listed in Settings > Apps: the machine-wide uninstall entries (64- and 32-bit)
/// plus the per-user entries of every user whose profile is loaded (signed in). Updates, hotfixes and hidden system
/// components are left out. Read at most every 10 minutes.
/// </summary>
internal sealed class SoftwareInventoryProvider
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly Regex UserSid = new(@"^S-1-5-21-[\d-]+$", RegexOptions.Compiled);
    private static readonly string[] UpdateReleaseTypes = { "Update", "Hotfix", "Security Update", "Update Rollup", "Service Pack" };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private RemoteSoftwareList? _cached;

    public async Task<RemoteSoftwareList> GetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_cached is null || DateTime.UtcNow - _cached.CollectedUtc > Lifetime)
            {
                _cached = await Task.Run(Read);
            }

            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static RemoteSoftwareList Read()
    {
        List<RemoteProgram> programs = new();
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            ReadUninstallKey(machine, "All users", programs);
        }

        foreach (string sid in Registry.Users.GetSubKeyNames().Where(name => UserSid.IsMatch(name)))
        {
            try
            {
                using RegistryKey? user = Registry.Users.OpenSubKey(sid);
                if (user is not null)
                {
                    ReadUninstallKey(user, UserName(sid), programs);
                }
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A profile being unloaded right now is skipped.
            }
        }

        return new RemoteSoftwareList { CollectedUtc = DateTime.UtcNow, Programs = Deduplicate(programs) };
    }

    private static void ReadUninstallKey(RegistryKey root, string installedFor, List<RemoteProgram> programs)
    {
        using RegistryKey? uninstall = root.OpenSubKey(UninstallKey);
        if (uninstall is null)
        {
            return;
        }

        foreach (string name in uninstall.GetSubKeyNames())
        {
            try
            {
                using RegistryKey? entry = uninstall.OpenSubKey(name);
                if (entry is null)
                {
                    continue;
                }

                Dictionary<string, object?> values = entry.GetValueNames().ToDictionary(value => value, value => entry.GetValue(value), StringComparer.OrdinalIgnoreCase);
                if (ToProgram(values, installedFor) is { } program)
                {
                    programs.Add(program);
                }
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
    }

    /// <summary>The program an uninstall entry describes, or null for updates, hidden components and nameless entries.</summary>
    internal static RemoteProgram? ToProgram(IReadOnlyDictionary<string, object?> values, string installedFor)
    {
        string name = Text(values, "DisplayName");
        if (name.Length == 0
            || values.TryGetValue("SystemComponent", out object? hidden) && hidden is int flag && flag == 1
            || Text(values, "ParentKeyName").Length > 0
            || UpdateReleaseTypes.Contains(Text(values, "ReleaseType"), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        string date = Text(values, "InstallDate");
        return new RemoteProgram
        {
            Name = name,
            Version = Text(values, "DisplayVersion"),
            Publisher = Text(values, "Publisher"),
            InstalledOn = DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime installed) ? installed : null,
            SizeBytes = values.TryGetValue("EstimatedSize", out object? size) && size is int kilobytes && kilobytes > 0 ? kilobytes * 1024L : null,
            InstalledFor = installedFor
        };
    }

    /// <summary>One row per name and version (64- and 32-bit entries of the same program, or two users with the same app).</summary>
    internal static List<RemoteProgram> Deduplicate(IEnumerable<RemoteProgram> programs) => programs
        .GroupBy(program => (program.Name.ToUpperInvariant(), program.Version.ToUpperInvariant()))
        .Select(group =>
        {
            RemoteProgram first = group.First();
            List<string> owners = group.Select(program => program.InstalledFor).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            first.InstalledFor = owners.Contains("All users") ? "All users" : string.Join(", ", owners);
            return first;
        })
        .OrderBy(program => program.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    private static string Text(IReadOnlyDictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out object? value) ? (value?.ToString() ?? string.Empty).Trim() : string.Empty;

    private static string UserName(string sid)
    {
        try
        {
            string account = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            return account.Contains('\\') ? account[(account.IndexOf('\\') + 1)..] : account;
        }
        catch (SystemException)
        {
            // Deleted or domain account that cannot be looked up now.
            return sid;
        }
    }
}
