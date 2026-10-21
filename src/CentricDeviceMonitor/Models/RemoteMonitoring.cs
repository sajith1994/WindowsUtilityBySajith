using System.Security.Cryptography;
using System.Text;

namespace CentricDeviceMonitor.Models;

/// <summary>
/// Remote monitoring between copies of the app on the local network. A PC that allows monitoring runs a small
/// read-only endpoint in its background service; the primary PC asks it for stats. Every stats request is signed
/// with the client's pairing code (HMAC-SHA256 over the client's clock and the path), so the code itself never
/// crosses the network and nothing on the network can read stats without it.
/// </summary>
public static class RemoteMonitoringProtocol
{
    public const int Version = 1;
    public const int DefaultPort = 47810;
    public const string HelloPath = "/wu/v1/hello";
    public const string StatsPath = "/wu/v1/stats";

    /// <summary>GET ?mb=N: test data for the LAN download speed (this PC reading from the monitored PC).</summary>
    public const string SpeedDownloadPath = "/wu/v1/speed/download";

    /// <summary>POST: test data for the LAN upload speed (this PC sending to the monitored PC).</summary>
    public const string SpeedUploadPath = "/wu/v1/speed/upload";

    /// <summary>GET: the monitored PC measures its own internet speed and reports it.</summary>
    public const string InternetSpeedPath = "/wu/v1/speed/internet";

    /// <summary>Paths that need a signed request.</summary>
    /// <summary>POST: the monitored PC updates itself to the latest official release (downloaded and verified by it).</summary>
    public const string UpdatePath = "/wu/v1/update";

    /// <summary>GET: programs using the PC now, who is signed in, last restart and the last 30 days of problems.</summary>
    public const string DetailsPath = "/wu/v1/details";

    /// <summary>Paths that need a signed request.</summary>
    public static readonly string[] SignedPaths = { StatsPath, SpeedDownloadPath, SpeedUploadPath, InternetSpeedPath, UpdatePath, DetailsPath };

    /// <summary>Paths that take POST instead of GET.</summary>
    public static readonly string[] PostPaths = { SpeedUploadPath, UpdatePath };
    public const string TimeHeader = "X-WU-Time";
    public const string SignatureHeader = "X-WU-Signature";
    public const string FirewallRuleName = "Windows Utility remote monitoring";

    /// <summary>Signed requests older or newer than this (on the client's clock) are refused.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    // No 0/O or 1/I so a code read off one screen and typed on another is not mistyped.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>A new 12-character pairing code (60 bits), shown as XXXX-XXXX-XXXX.</summary>
    public static string NewPairingCode()
    {
        StringBuilder code = new();
        for (int index = 0; index < 12; index++)
        {
            if (index > 0 && index % 4 == 0)
            {
                code.Append('-');
            }

            code.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        }

        return code.ToString();
    }

    /// <summary>Upper case with spaces and dashes removed, so "abcd efgh-ijkl" matches "ABCD-EFGH-IJKL".</summary>
    public static string NormalizeCode(string? code) =>
        new((code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Shortest code accepted (letters and digits after normalizing).</summary>
    public const int MinimumCodeLength = 8;

    public static bool CodesMatch(string? first, string? second) =>
        NormalizeCode(first) is { Length: > 0 } normalized && normalized == NormalizeCode(second);

    /// <summary>Fingerprint of a code, so the service can tell when it changed without storing it twice.</summary>
    public static string Fingerprint(string? code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCode(code))));

    public static string Sign(string code, long unixSeconds, string path) =>
        Convert.ToBase64String(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(NormalizeCode(code)),
            Encoding.UTF8.GetBytes($"{unixSeconds}\n{path}")));

    public static bool Verify(string code, string? time, string? signature, string path, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(NormalizeCode(code)) || !long.TryParse(time, out long unixSeconds) || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        if (Math.Abs(now.ToUnixTimeSeconds() - unixSeconds) > MaxClockSkew.TotalSeconds)
        {
            return false;
        }

        byte[] expected = Encoding.ASCII.GetBytes(Sign(code, unixSeconds, path));
        byte[] actual = Encoding.ASCII.GetBytes(signature);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

/// <summary>Unauthenticated identity answer, used to find PCs and to line up the two clocks.</summary>
public sealed class RemoteHello
{
    public int ProtocolVersion { get; set; } = RemoteMonitoringProtocol.Version;
    public string MachineName { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public long ServerTimeUnix { get; set; }
}

/// <summary>What a monitored PC reports.</summary>
public sealed class RemoteStatsPayload
{
    public int ProtocolVersion { get; set; } = RemoteMonitoringProtocol.Version;
    public string MachineName { get; set; } = string.Empty;
    public string OsDisplay { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public long UptimeSeconds { get; set; }
    public DateTime CollectedAtUtc { get; set; }

    /// <summary>False when the service has not refreshed its readings recently (values may be stale).</summary>
    public bool ReadingsFresh { get; set; }

    public string CpuModel { get; set; } = string.Empty;
    public double? CpuUsagePercent { get; set; }
    public double? CpuTemperatureCelsius { get; set; }
    public bool CpuTemperatureIsCpuSpecific { get; set; }
    public string CpuTemperatureDisplay { get; set; } = string.Empty;
    public double CpuTemperatureAlertThresholdCelsius { get; set; } = 98.0;
    public double? CpuFanRpm { get; set; }
    public double? CpuPackagePowerWatts { get; set; }
    public double? GpuPowerWatts { get; set; }

    public double RamUsagePercent { get; set; }
    public ulong TotalMemoryBytes { get; set; }
    public ulong AvailableMemoryBytes { get; set; }

    public bool NetworkConnected { get; set; }
    public string NetworkSummary { get; set; } = string.Empty;

    public List<RemoteDiskInfo> Disks { get; set; } = new();

    /// <summary>Problems logged in the last 7 days (null from PCs on 2.0.73 or older, or before the first check).</summary>
    public RemoteProblemSummary? Problems { get; set; }

    /// <summary>Windows Update, antivirus and firewall state (null from PCs on 2.0.68 or older, or before the first check).</summary>
    public RemoteSecurityInfo? Security { get; set; }

    /// <summary>PC maker and model, e.g. "LENOVO" and "ThinkPad X1 Carbon Gen 9 (20XW005JUK)".</summary>
    public string SystemManufacturer { get; set; } = string.Empty;
    public string SystemModel { get; set; } = string.Empty;

    public bool HasBattery { get; set; }
    public int? BatteryPercent { get; set; }

    /// <summary>"Charging", "Plugged in" or "On battery".</summary>
    public string BatteryState { get; set; } = string.Empty;
    public int? BatteryRuntimeMinutes { get; set; }
    public double? BatteryDesignMWh { get; set; }
    public double? BatteryFullMWh { get; set; }
    public double? BatteryHealthPercent { get; set; }
    public uint? BatteryCycleCount { get; set; }
    /// <summary>Progress of an update started from another PC ("Downloading 2.0.68...", "Update failed: ..."); empty otherwise.</summary>
    public string UpdateStatus { get; set; } = string.Empty;

    /// <summary>Live disk activity (empty from PCs on 2.0.66 or older).</summary>
    public List<DiskActivityInfo> DiskActivity { get; set; } = new();

    /// <summary>Live network activity per connected adapter (empty from PCs on 2.0.66 or older).</summary>
    public List<NetworkActivityInfo> NetworkActivity { get; set; } = new();
}

public sealed class RemoteDiskInfo
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
}

/// <summary>One physical disk's activity: active time and transfer rates (as Task Manager shows them).</summary>
public sealed class DiskActivityInfo
{
    /// <summary>"Disk 0 (C:)".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Drive letters on the disk, "C:" or "D: E:".</summary>
    public string Letters { get; set; } = string.Empty;

    public double ActivePercent { get; set; }
    public double ReadBytesPerSec { get; set; }
    public double WriteBytesPerSec { get; set; }
}

/// <summary>One connected network adapter's receive and send rates.</summary>
public sealed class NetworkActivityInfo
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public double ReceiveBitsPerSec { get; set; }
    public double SendBitsPerSec { get; set; }
    public double LinkBitsPerSec { get; set; }
}

/// <summary>Counts of problems Windows logged on a monitored PC (from the System and Application event logs).</summary>
public sealed class RemoteProblemSummary
{
    public int Days { get; set; }
    public int BlueScreens { get; set; }
    public int UnexpectedShutdowns { get; set; }
    public int AppCrashes { get; set; }
    public int AppFreezes { get; set; }
    public int DiskErrors { get; set; }
    public int HardwareErrors { get; set; }

    /// <summary>Blue screens, unexpected shutdowns, disk or hardware errors: worth looking at soon.</summary>
    public bool HasSerious => BlueScreens + UnexpectedShutdowns + DiskErrors + HardwareErrors > 0;

    public bool HasAny => HasSerious || AppCrashes + AppFreezes > 0;

    /// <summary>"1 blue screen • 3 app crashes"; empty when nothing was logged.</summary>
    public string Describe()
    {
        List<string> parts = new();
        void Add(int count, string one, string many)
        {
            if (count > 0)
            {
                parts.Add($"{count} {(count == 1 ? one : many)}");
            }
        }

        Add(BlueScreens, "blue screen", "blue screens");
        Add(UnexpectedShutdowns, "unexpected shutdown", "unexpected shutdowns");
        Add(DiskErrors, "disk error", "disk errors");
        Add(HardwareErrors, "hardware error", "hardware errors");
        Add(AppCrashes, "app crash", "app crashes");
        Add(AppFreezes, "app freeze", "app freezes");
        return string.Join("  •  ", parts);
    }
}

/// <summary>One problem from the event logs.</summary>
public sealed class RemoteProblem
{
    public DateTime TimeUtc { get; set; }

    /// <summary>BlueScreen, UnexpectedShutdown, AppCrash, AppFreeze, DiskError or HardwareError.</summary>
    public string Kind { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

/// <summary>A program (all its processes together) and what it uses right now.</summary>
public sealed class RemoteProcessInfo
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Count { get; set; }
    public double CpuPercent { get; set; }
    public long MemoryBytes { get; set; }
}

/// <summary>A signed-in Windows session.</summary>
public sealed class RemoteSessionInfo
{
    public string User { get; set; } = string.Empty;

    /// <summary>Active, Locked or Disconnected.</summary>
    public string State { get; set; } = string.Empty;
}

/// <summary>Details of a monitored PC, fetched on demand for the Details window.</summary>
public sealed class RemotePcDetails
{
    public string MachineName { get; set; } = string.Empty;
    public DateTime LastBootUtc { get; set; }
    public List<RemoteSessionInfo> Sessions { get; set; } = new();
    public List<RemoteProcessInfo> TopByCpu { get; set; } = new();
    public List<RemoteProcessInfo> TopByMemory { get; set; } = new();
    public RemoteProblemSummary? Last7Days { get; set; }
    public RemoteProblemSummary? Last30Days { get; set; }

    /// <summary>Newest first, at most 100.</summary>
    public List<RemoteProblem> Problems { get; set; } = new();
}

/// <summary>Windows Update, antivirus and firewall state of a monitored PC.</summary>
public sealed class RemoteSecurityInfo
{
    public DateTime CheckedUtc { get; set; }

    /// <summary>Newest successfully installed update, definition updates excluded.</summary>
    public DateTime? LastUpdateInstalledUtc { get; set; }
    public string LastUpdateTitle { get; set; } = string.Empty;

    /// <summary>Updates waiting to install, from Windows Update's last scan (null when it could not be read).</summary>
    public int? PendingUpdates { get; set; }
    public bool RestartRequired { get; set; }

    public string AntivirusName { get; set; } = string.Empty;
    public bool? AntivirusOn { get; set; }
    public bool? RealTimeProtectionOn { get; set; }
    public DateTime? DefinitionsUpdatedUtc { get; set; }

    /// <summary>Third-party antivirus: whether Security Center reports its definitions as current.</summary>
    public bool? DefinitionsUpToDate { get; set; }
    public DateTime? LastScanUtc { get; set; }

    public bool? FirewallOn { get; set; }

    /// <summary>Profiles with the firewall off, e.g. "Public".</summary>
    public string FirewallOffProfiles { get; set; } = string.Empty;
}
/// <summary>Answer to an update request.</summary>
public sealed class RemoteUpdateResult
{
    /// <summary>UpToDate, Started, Busy or Failed.</summary>
    public string Status { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>What the monitored PC received during a LAN upload test.</summary>
public sealed class RemoteUploadResult
{
    public long Bytes { get; set; }
    public long Milliseconds { get; set; }
}

/// <summary>The monitored PC's own internet speed.</summary>
public sealed class RemoteInternetSpeedResult
{
    public double? DownloadMbps { get; set; }
    public double? UploadMbps { get; set; }
    public double? LatencyMs { get; set; }
    public string Server { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

/// <summary>A PC that has read this PC's stats with the current pairing code.</summary>
public sealed class RemoteViewerInfo
{
    public string Address { get; set; } = string.Empty;
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>Viewers of this PC, saved by the service with the fingerprint of the code they used.</summary>
public sealed class RemoteViewerStore
{
    public string CodeFingerprint { get; set; } = string.Empty;
    public List<RemoteViewerInfo> Viewers { get; set; } = new();
}

/// <summary>A PC added on the primary PC's Network PCs page.</summary>
public sealed class RemotePcEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = RemoteMonitoringProtocol.DefaultPort;
    public string PairingCode { get; set; } = string.Empty;
}
