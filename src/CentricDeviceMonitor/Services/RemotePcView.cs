using System.ComponentModel;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

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

    public string Title => !string.IsNullOrWhiteSpace(Entry.DisplayName) ? Entry.DisplayName : Stats?.MachineName ?? Entry.Host;

    public string Subtitle => Stats is { } stats
        ? $"{stats.OsDisplay}  •  up {FormatUptime(stats.UptimeSeconds)}"
        : Entry.Port == RemoteMonitoringProtocol.DefaultPort ? Entry.Host : $"{Entry.Host}:{Entry.Port}";

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
        _result = result;
        if (result.State == RemoteStatsState.Online)
        {
            _lastSeen = DateTime.Now;
            LastKnownStats = result.Stats;
            if (_updateRequested && (!string.IsNullOrEmpty(result.Stats?.UpdateStatus) || !IsOutdatedFor(result.Stats)))
            {
                _updateRequested = false;
            }
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
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
