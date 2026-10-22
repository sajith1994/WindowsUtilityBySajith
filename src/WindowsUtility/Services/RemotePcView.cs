using System.ComponentModel;
using WindowsUtility.Models;

namespace WindowsUtility.Services;

/// <summary>One tile on the Network PCs page. Each poll replaces the result and refreshes every binding at once.</summary>
public sealed class RemotePcView : INotifyPropertyChanged
{
    private RemoteStatsResult? _result;
    private DateTime? _lastSeen;

    public RemotePcView(RemotePcEntry entry)
    {
        Entry = entry;
    }

    public RemotePcEntry Entry { get; set; }

    public RemoteStatsPayload? Stats => _result?.Stats;

    /// <summary>The last readings received, kept while the PC is offline (used by the inventory report).</summary>
    public RemoteStatsPayload? LastKnownStats { get; private set; }

    public DateTime? LastSeen => _lastSeen;

    public bool IsOnline => _result?.State == RemoteStatsState.Online;

    public string StateMessage => _result?.Message ?? "Not checked yet";

    public bool HasStats => Stats is not null;

    public string Group => Entry.Group.Trim();

    public bool HasGroup => Group.Length > 0;

    /// <summary>Reached over Tailscale rather than the local network.</summary>
    public bool ViaVpn => VpnAddresses.IsVpnHost(Entry.Host);

    /// <summary>Problems first: offline, then warnings, then the rest (for "Problems first" sorting).</summary>
    public int SortRank => StatusKind switch
    {
        "Offline" => 0,
        "Warning" => 1,
        "Checking" => 2,
        _ => 3
    };

    /// <summary>Everything the search box matches: names, address, group, model, processor and signed-in users.</summary>
    public string SearchText => string.Join(" ", Title, Entry.Host, Group, Stats?.MachineName, ModelText, CpuModel, SessionsText, Stats?.OsDisplay);

    public string Title => !string.IsNullOrWhiteSpace(Entry.DisplayName) ? Entry.DisplayName : Stats?.MachineName ?? Entry.Host;

    public string Subtitle => (Stats is { } stats
        ? $"{stats.OsDisplay}  •  up {FormatUptime(stats.UptimeSeconds)}"
        : Entry.Port == RemoteMonitoringProtocol.DefaultPort ? Entry.Host : $"{Entry.Host}:{Entry.Port}")
        + (ViaVpn ? "  •  via VPN" : string.Empty);

    public string AddressToolTip => Stats is { } stats
        ? $"{stats.MachineName} at {Entry.Host}:{Entry.Port}  •  Windows Utility {stats.AppVersion}"
        : $"{Entry.Host}:{Entry.Port}";

    /// <summary>Online, Warning, Offline or Checking: drives the status pill colour.</summary>
    public string StatusKind => _result?.State switch
    {
        null => "Checking",
        RemoteStatsState.Online when Stats is { ReadingsFresh: false } => "Warning",
        RemoteStatsState.Online when IsHot || Disks.Any(disk => disk.IsLow) || ProtectionAtRisk => "Warning",
        RemoteStatsState.Online => "Online",
        RemoteStatsState.WrongCode or RemoteStatsState.NotSharing => "Warning",
        _ => "Offline"
    };

    public string StatusLabel => _result?.State switch
    {
        null => "Checking",
        RemoteStatsState.Online when IsHot => "Hot",
        RemoteStatsState.Online when Disks.Any(disk => disk.IsLow) => "Disk low",
        RemoteStatsState.Online when ProtectionAtRisk => "At risk",
        RemoteStatsState.Online => "Online",
        RemoteStatsState.WrongCode => "Code",
        RemoteStatsState.NotSharing => "Not sharing",
        RemoteStatsState.Unreachable => "Offline",
        _ => "Error"
    };

    public string Message => _result is null
        ? "Connecting..."
        : _result.State == RemoteStatsState.Online ? string.Empty : _result.Message;

    public double CpuPercent => Stats?.CpuUsagePercent ?? 0;

    public string CpuText => Stats?.CpuUsagePercent is double cpu ? $"{cpu:0}%" : "--";

    public string CpuModel => Stats?.CpuModel ?? string.Empty;

    public bool IsHot => Stats is { CpuTemperatureIsCpuSpecific: true, CpuTemperatureCelsius: double temperature } stats
        && temperature >= stats.CpuTemperatureAlertThresholdCelsius;

    public string TemperatureText => Stats is { } stats && stats.CpuTemperatureCelsius is double temperature
        ? stats.CpuTemperatureIsCpuSpecific ? $"{temperature:0} °C" : $"{temperature:0} °C (system)"
        : "Not available";

    public string TemperatureDetail
    {
        get
        {
            if (Stats is not { } stats)
            {
                return string.Empty;
            }

            List<string> parts = new();
            if (stats.CpuFanRpm is double fan && fan > 0)
            {
                parts.Add($"fan {fan:0} RPM");
            }

            if (stats.CpuPackagePowerWatts is double cpuWatts && cpuWatts > 0)
            {
                parts.Add($"CPU {cpuWatts:0} W");
            }

            if (stats.GpuPowerWatts is double gpuWatts && gpuWatts > 0)
            {
                parts.Add($"GPU {gpuWatts:0} W");
            }

            return string.Join("  •  ", parts);
        }
    }

    /// <summary>"Dell Inc. Latitude 5420"; empty when the PC does not report it (or runs 2.0.67 or older).</summary>
    public string ModelText
    {
        get
        {
            string maker = Stats?.SystemManufacturer.Trim() ?? string.Empty;
            string model = Stats?.SystemModel.Trim() ?? string.Empty;
            return model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? model : $"{maker} {model}".Trim();
        }
    }

    public bool HasModel => ModelText.Length > 0;

    public bool HasBattery => Stats?.HasBattery == true;

    public double BatteryPercent => Stats?.BatteryPercent ?? 0;

    public string BatteryText => Stats?.BatteryPercent is int percent ? $"{percent}%" : "--";

    /// <summary>"On battery • 2 h 10 m left • 49.1 of 57.0 Wh (86% health) • 312 cycles".</summary>
    public string BatteryDetail
    {
        get
        {
            if (Stats is not { HasBattery: true } stats)
            {
                return string.Empty;
            }

            List<string> parts = new() { stats.BatteryState };
            if (stats.BatteryRuntimeMinutes is int minutes)
            {
                parts.Add(minutes >= 60 ? $"{minutes / 60} h {minutes % 60} m left" : $"{minutes} m left");
            }

            if (stats.BatteryDesignMWh is double design)
            {
                parts.Add(stats.BatteryFullMWh is double full
                    ? $"{full / 1000:0.0} of {design / 1000:0.0} Wh ({stats.BatteryHealthPercent:0}% health)"
                    : $"{design / 1000:0.0} Wh design");
            }

            if (stats.BatteryCycleCount is uint cycles)
            {
                parts.Add($"{cycles} cycles");
            }

            return string.Join("  •  ", parts.Where(part => part.Length > 0));
        }
    }

    /// <summary>Battery worn below 60% of its design capacity.</summary>
    public bool BatteryWorn => Stats?.BatteryHealthPercent is < 60;
    // ---- Signed-in users and actions ----

    /// <summary>The PC accepts actions (restart, lock, message...) from this PC.</summary>
    public bool ActionsAllowed => IsOnline && Stats?.ActionsAllowed == true;

    public bool HasSessions => Stats?.Sessions.Count > 0;

    /// <summary>"Signed in: OFFICE\maria (locked)" or "Nobody signed in".</summary>
    public string SessionsText => Stats is null
        ? string.Empty
        : Stats.Sessions.Count == 0
            ? "Nobody signed in"
            : "Signed in: " + string.Join(", ", Stats.Sessions.Select(session => $"{session.User} ({session.State.ToLowerInvariant()})"));

    /// <summary>Someone is actively using the PC (restart and shutdown warn about it).</summary>
    public bool SomeoneActive => Stats?.Sessions.Any(session => session.State == "Active") == true;

    // ---- Problems from the event logs ----

    public bool HasProblems => Stats?.Problems?.HasAny == true;

    /// <summary>Blue screens, unexpected shutdowns, disk or hardware errors (red); otherwise app crashes only (amber).</summary>
    public bool ProblemsSerious => Stats?.Problems?.HasSerious == true;

    public string ProblemsText => Stats?.Problems is { HasAny: true } problems ? problems.Describe() : string.Empty;

    // ---- Restore points and backups ----

    public const int RestorePointStaleDays = 30;

    public bool HasRecovery => Stats?.Recovery is not null;

    /// <summary>System Protection off, or no restore point in the last 30 days.</summary>
    public bool RecoveryNeedsAttention => Stats?.Recovery is { } recovery && RecoveryStale(recovery);

    internal static bool RecoveryStale(RemoteRecoveryInfo recovery) =>
        recovery.SystemProtectionOn == false
        || recovery.LastRestorePointUtc is not DateTime last
        || DateTime.UtcNow - last > TimeSpan.FromDays(RestorePointStaleDays);

    /// <summary>"Restore point 3 days ago • backup yesterday" or "System Protection off • no restore point".</summary>
    public string RecoveryText
    {
        get
        {
            if (Stats?.Recovery is not { } recovery)
            {
                return string.Empty;
            }

            List<string> parts = new();
            if (recovery.SystemProtectionOn == false)
            {
                parts.Add("System Protection off");
            }

            parts.Add(recovery.LastRestorePointUtc is DateTime last ? $"Restore point {Ago(last)}" : "No restore point");
            if (recovery.LastBackupUtc is DateTime backup)
            {
                parts.Add($"Windows Backup {Ago(backup)}");
            }

            return string.Join("  •  ", parts);
        }
    }

    public string RecoveryToolTip => Stats?.Recovery is { LastRestorePointDescription.Length: > 0 } recovery
        ? $"{recovery.RestorePointCount} restore point{(recovery.RestorePointCount == 1 ? "" : "s")}; newest: {recovery.LastRestorePointDescription}"
        : string.Empty;

    // ---- Waking ----

    private DateTime? _wakeSentAt;

    /// <summary>
    /// Offline (not just not sharing), an adapter address is known from when it was last online, and it is on this
    /// network (a wake signal does not cross a VPN or router).
    /// </summary>
    public bool CanWake => _result?.State == RemoteStatsState.Unreachable && Entry.WakeMacAddresses.Count > 0 && !ViaVpn;

    public bool IsWaking => _wakeSentAt is DateTime sent && DateTime.Now - sent < TimeSpan.FromMinutes(3) && !IsOnline;

    public string WakeText => IsWaking
        ? $"Wake signal sent at {_wakeSentAt:HH:mm}. Waiting for {Title} to start (up to 3 minutes)..."
        : CanWake && Entry.WakeOnLanEnabled == false
            ? "Its network adapter has \"Wake on magic packet\" turned off, so Wake may not work."
            : string.Empty;

    public bool HasWakeText => WakeText.Length > 0;

    public void MarkWakeSent()
    {
        _wakeSentAt = DateTime.Now;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    // ---- Security (Windows Update, antivirus, firewall) ----

    public const int UpdatesStaleDays = 30;

    public RemoteSecurityInfo? Security => Stats?.Security;

    public bool HasSecurity => Security is not null;

    /// <summary>Antivirus or real-time protection or firewall off.</summary>
    public bool ProtectionAtRisk => Security is { } security
        && (security.AntivirusOn == false || security.RealTimeProtectionOn == false || security.FirewallOn == false);

    /// <summary>No Windows update for a month, or a restart waiting to finish updates, or old definitions.</summary>
    public bool UpdatesNeedAttention => Security is { } security
        && (UpdatesStale(security) || security.RestartRequired || DefinitionsStale(security));

    internal static bool UpdatesStale(RemoteSecurityInfo security) =>
        security.LastUpdateInstalledUtc is DateTime installed && DateTime.UtcNow - installed > TimeSpan.FromDays(UpdatesStaleDays);

    private static bool DefinitionsStale(RemoteSecurityInfo security) =>
        security.DefinitionsUpToDate == false
        || security.DefinitionsUpdatedUtc is DateTime updated && DateTime.UtcNow - updated > TimeSpan.FromDays(7);

    /// <summary>"Updated 6 days ago • 2 pending • restart needed".</summary>
    public string UpdatesText
    {
        get
        {
            if (Security is not { } security)
            {
                return string.Empty;
            }

            List<string> parts = new()
            {
                security.LastUpdateInstalledUtc is DateTime installed ? $"Windows updated {Ago(installed)}" : "Last Windows update unknown"
            };
            if (security.PendingUpdates is > 0)
            {
                parts.Add($"{security.PendingUpdates} pending");
            }

            if (security.RestartRequired)
            {
                parts.Add("restart needed");
            }

            return string.Join("  •  ", parts);
        }
    }

    public string UpdatesToolTip => Security?.LastUpdateTitle is { Length: > 0 } title ? $"Last installed: {title}" : string.Empty;

    /// <summary>"Microsoft Defender on • definitions today • scanned yesterday • firewall on".</summary>
    public string ProtectionText
    {
        get
        {
            if (Security is not { } security)
            {
                return string.Empty;
            }

            List<string> parts = new();
            if (security.AntivirusOn == true)
            {
                parts.Add(security.RealTimeProtectionOn == false ? $"{security.AntivirusName} on, real-time protection OFF" : $"{security.AntivirusName} on");
                if (security.DefinitionsUpdatedUtc is DateTime definitions)
                {
                    parts.Add($"definitions {Ago(definitions)}");
                }
                else if (security.DefinitionsUpToDate == false)
                {
                    parts.Add("definitions out of date");
                }

                if (security.LastScanUtc is DateTime scan)
                {
                    parts.Add($"scanned {Ago(scan)}");
                }
            }
            else
            {
                parts.Add(security.AntivirusName is { Length: > 0 } name && name != "No antivirus found" ? $"{name} OFF" : "No antivirus running");
            }

            if (security.FirewallOn == true)
            {
                parts.Add("firewall on");
            }
            else if (security.FirewallOn == false)
            {
                parts.Add($"firewall OFF ({security.FirewallOffProfiles})");
            }

            return string.Join("  •  ", parts);
        }
    }

    private static string Ago(DateTime utc)
    {
        TimeSpan age = DateTime.UtcNow - utc;
        DateTime local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? "today"
            : local.Date == DateTime.Today.AddDays(-1) ? "yesterday"
            : age.TotalDays < 60 ? $"{(int)age.TotalDays} days ago"
            : $"on {local:d MMM yyyy}";
    }

    public double RamPercent => Stats?.RamUsagePercent ?? 0;

    public string RamText => Stats is null ? "--" : $"{Stats.RamUsagePercent:0}%";

    public string RamDetail => Stats is { TotalMemoryBytes: > 0 } stats
        ? $"{FormatGb(stats.TotalMemoryBytes - Math.Min(stats.AvailableMemoryBytes, stats.TotalMemoryBytes))} of {FormatGb(stats.TotalMemoryBytes)} used"
        : string.Empty;

    public IReadOnlyList<RemoteDiskView> Disks => Stats?.Disks
        .Select(disk => new RemoteDiskView(disk, Stats.DiskActivity.FirstOrDefault(activity =>
            activity.Letters.Split(' ').Contains(disk.Name, StringComparer.OrdinalIgnoreCase))))
        .ToList() ?? new List<RemoteDiskView>();

    public bool HasNetworkActivity => Stats?.NetworkActivity.Count > 0;

    /// <summary>Total receive and send rate over all connected adapters.</summary>
    public string NetworkText => Stats is { NetworkActivity.Count: > 0 } stats
        ? $"↓ {ActivityMonitor.FormatBitsPerSecond(stats.NetworkActivity.Sum(adapter => adapter.ReceiveBitsPerSec))}   ↑ {ActivityMonitor.FormatBitsPerSecond(stats.NetworkActivity.Sum(adapter => adapter.SendBitsPerSec))}"
        : string.Empty;

    public string NetworkDetail => Stats is { NetworkActivity.Count: > 0 } stats
        ? string.Join("  •  ", stats.NetworkActivity.Select(adapter => adapter.LinkBitsPerSec > 0
            ? $"{adapter.Name} ({ActivityMonitor.FormatBitsPerSecond(adapter.LinkBitsPerSec)} link)"
            : adapter.Name))
        : string.Empty;

    /// <summary>The PC's Windows Utility version is older than this PC's.</summary>
    public bool IsOutdated => IsOutdatedFor(Stats);

    private static bool IsOutdatedFor(RemoteStatsPayload? stats) =>
        stats is not null && Version.TryParse(stats.AppVersion, out Version? theirs) && theirs < UpdateService.GetCurrentVersion();

    public bool IsUpdating => !string.IsNullOrEmpty(Stats?.UpdateStatus) && !Stats!.UpdateStatus.StartsWith("Update failed", StringComparison.Ordinal);

    /// <summary>Update can be asked for: online, older than this PC and not already updating.</summary>
    public bool CanUpdate => _result?.State == RemoteStatsState.Online && IsOutdated && !IsUpdating && !_updateRequested;

    public string UpdateText => !string.IsNullOrEmpty(Stats?.UpdateStatus)
        ? Stats!.UpdateStatus
        : _updateRequested ? "Update requested..."
        : IsOutdated ? $"Windows Utility {Stats!.AppVersion} (this PC has {UpdateService.GetCurrentVersion().ToString(3)})" : string.Empty;

    public bool HasUpdateText => UpdateText.Length > 0;

    private bool _updateRequested;

    /// <summary>Set while a request is on its way; cleared when the PC reports progress or a newer version.</summary>
    public void MarkUpdateRequested(bool requested)
    {
        _updateRequested = requested;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public string UpdatedText => _result?.State == RemoteStatsState.Online
        ? $"Updated {DateTime.Now:HH:mm:ss}  •  v{Stats?.AppVersion}"
        : _lastSeen is DateTime seen ? $"Last seen {seen:dd/MM HH:mm}" : string.Empty;

    public void Update(RemoteStatsResult result)
    {
        int rank = _result is null ? -1 : SortRank;
        _result = result;
        if (result.State == RemoteStatsState.Online)
        {
            _lastSeen = DateTime.Now;
            _wakeSentAt = null;
            LastKnownStats = result.Stats;
            if (_updateRequested && (!string.IsNullOrEmpty(result.Stats?.UpdateStatus) || !IsOutdatedFor(result.Stats)))
            {
                _updateRequested = false;
            }
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        if (rank != SortRank)
        {
            // Named as well, so "Problems first" sorting moves only this tile.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SortRank)));
        }
    }

    /// <summary>
    /// Wired adapter addresses (then Wi-Fi) the PC reported, for waking it later. Returns true when they changed, so
    /// the caller saves them.
    /// </summary>
    public bool RememberWakeDetails()
    {
        if (Stats is not { Adapters.Count: > 0 } stats)
        {
            return false;
        }

        List<string> macs = stats.Adapters.Where(adapter => adapter.MacAddress.Length > 0)
            .OrderByDescending(adapter => adapter.IsWired).Select(adapter => adapter.MacAddress).Distinct().ToList();
        bool? wiredWake = stats.Adapters.Where(adapter => adapter.IsWired).Select(adapter => adapter.WakeOnMagicPacket).FirstOrDefault(wake => wake.HasValue);
        string address = System.Net.IPAddress.TryParse(Entry.Host, out _) ? Entry.Host : Entry.LastKnownAddress;
        if (macs.SequenceEqual(Entry.WakeMacAddresses, StringComparer.OrdinalIgnoreCase) && wiredWake == Entry.WakeOnLanEnabled)
        {
            return false;
        }

        Entry.WakeMacAddresses = macs;
        Entry.WakeOnLanEnabled = wiredWake;
        Entry.LastKnownAddress = address;
        return true;
    }

    /// <summary>After editing the entry (name, address or code): show Checking until the next poll.</summary>
    public void Reset(RemotePcEntry entry)
    {
        Entry = entry;
        _result = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal static string FormatGb(ulong bytes) => FormatGb((double)bytes);

    internal static string FormatGb(double bytes) => bytes >= 1024d * 1024 * 1024 * 1024
        ? $"{bytes / 1024 / 1024 / 1024 / 1024:0.0} TB"
        : $"{bytes / 1024 / 1024 / 1024:0.#} GB";

    private static string FormatUptime(long seconds)
    {
        TimeSpan uptime = TimeSpan.FromSeconds(seconds);
        return uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays} d {uptime.Hours} h"
            : uptime.TotalHours >= 1 ? $"{uptime.Hours} h {uptime.Minutes} m" : $"{Math.Max(1, uptime.Minutes)} m";
    }
}

public sealed class RemoteDiskView
{
    public RemoteDiskView(RemoteDiskInfo disk, DiskActivityInfo? activity = null)
    {
        HasActivity = activity is not null;
        ActivePercent = activity?.ActivePercent ?? 0;
        ActivityText = activity is null
            ? string.Empty
            : $"Active {activity.ActivePercent:0}%  •  read {ActivityMonitor.FormatBytesPerSecond(activity.ReadBytesPerSec)}  •  write {ActivityMonitor.FormatBytesPerSecond(activity.WriteBytesPerSec)}";
        double used = disk.TotalBytes > 0 ? 100.0 * (disk.TotalBytes - disk.FreeBytes) / disk.TotalBytes : 0;
        UsedPercent = Math.Clamp(used, 0, 100);
        Name = string.IsNullOrWhiteSpace(disk.Label) ? disk.Name : $"{disk.Name} {disk.Label}";
        PercentText = $"{UsedPercent:0}%";
        Detail = $"{RemotePcView.FormatGb(disk.FreeBytes)} free of {RemotePcView.FormatGb(disk.TotalBytes)}";
        IsLow = disk.TotalBytes > 0 && disk.FreeBytes < disk.TotalBytes * 0.10;
    }

    public string Name { get; }
    public bool HasActivity { get; }
    public double ActivePercent { get; }
    public string ActivityText { get; }
    public double UsedPercent { get; }
    public string PercentText { get; }
    public string Detail { get; }
    public bool IsLow { get; }
}
