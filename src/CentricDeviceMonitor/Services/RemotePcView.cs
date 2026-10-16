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
        RemoteStatsState.Online when IsHot || Disks.Any(disk => disk.IsLow) => "Warning",
        RemoteStatsState.Online => "Online",
        RemoteStatsState.WrongCode or RemoteStatsState.NotSharing => "Warning",
        _ => "Offline"
    };

    public string StatusLabel => _result?.State switch
    {
        null => "Checking",
        RemoteStatsState.Online when IsHot => "Hot",
        RemoteStatsState.Online when Disks.Any(disk => disk.IsLow) => "Disk low",
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

    public double RamPercent => Stats?.RamUsagePercent ?? 0;

    public string RamText => Stats is null ? "--" : $"{Stats.RamUsagePercent:0}%";

    public string RamDetail => Stats is { TotalMemoryBytes: > 0 } stats
        ? $"{FormatGb(stats.TotalMemoryBytes - Math.Min(stats.AvailableMemoryBytes, stats.TotalMemoryBytes))} of {FormatGb(stats.TotalMemoryBytes)} used"
        : string.Empty;

    public IReadOnlyList<RemoteDiskView> Disks => Stats?.Disks.Select(disk => new RemoteDiskView(disk)).ToList() ?? new List<RemoteDiskView>();

    public string UpdatedText => _result?.State == RemoteStatsState.Online
        ? $"Updated {DateTime.Now:HH:mm:ss}"
        : _lastSeen is DateTime seen ? $"Last seen {seen:dd/MM HH:mm}" : string.Empty;

    public void Update(RemoteStatsResult result)
    {
        _result = result;
        if (result.State == RemoteStatsState.Online)
        {
            _lastSeen = DateTime.Now;
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
    public RemoteDiskView(RemoteDiskInfo disk)
    {
        double used = disk.TotalBytes > 0 ? 100.0 * (disk.TotalBytes - disk.FreeBytes) / disk.TotalBytes : 0;
        UsedPercent = Math.Clamp(used, 0, 100);
        Name = string.IsNullOrWhiteSpace(disk.Label) ? disk.Name : $"{disk.Name} {disk.Label}";
        PercentText = $"{UsedPercent:0}%";
        Detail = $"{RemotePcView.FormatGb(disk.FreeBytes)} free of {RemotePcView.FormatGb(disk.TotalBytes)}";
        IsLow = disk.TotalBytes > 0 && disk.FreeBytes < disk.TotalBytes * 0.10;
    }

    public string Name { get; }
    public double UsedPercent { get; }
    public string PercentText { get; }
    public string Detail { get; }
    public bool IsLow { get; }
}
