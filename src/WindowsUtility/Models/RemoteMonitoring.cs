using System.Security.Cryptography;
using System.Text;

namespace WindowsUtility.Models;

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

    /// <summary>POST: the monitored PC updates itself to the latest official release (downloaded and verified by it).</summary>
    public const string UpdatePath = "/wu/v1/update";

    /// <summary>
    /// POST: run an action (restart, lock, message...) when the PC allows actions. The signature also covers the
    /// request body, and each request id is accepted only once.
    /// </summary>
    public const string ActionPath = "/wu/v1/action";

    /// <summary>What an action request signs: the path plus the SHA-256 of its body, so the body cannot be changed.</summary>
    public static string ActionSignedText(byte[] body) => $"{ActionPath}\n{Convert.ToHexString(SHA256.HashData(body))}";

    /// <summary>GET: programs using the PC now, who is signed in, last restart and the last 30 days of problems.</summary>
    public const string DetailsPath = "/wu/v1/details";

    /// <summary>GET ?days=1|7: the PC's own readings, recorded every minute by its service (kept for 8 days).</summary>
    public const string HistoryPath = "/wu/v1/history";

    /// <summary>GET: programs installed on the PC (for all users).</summary>
    public const string SoftwarePath = "/wu/v1/software";

    /// <summary>GET ?k=link key: the read-only phone page of the monitored PCs, served by the main PC.</summary>
    public const string PhonePagePath = "/wu/phone";

    /// <summary>GET ?k=link key: the data behind the phone page.</summary>
    public const string PhoneDataPath = "/wu/phone/data";

    /// <summary>Paths that need a signed request.</summary>
    public static readonly string[] SignedPaths = { StatsPath, SpeedDownloadPath, SpeedUploadPath, InternetSpeedPath, UpdatePath, DetailsPath, HistoryPath, SoftwarePath };

    /// <summary>Paths that take POST instead of GET.</summary>
    public static readonly string[] PostPaths = { SpeedUploadPath, UpdatePath, ActionPath };

    public const string TimeHeader = "X-WU-Time";
    public const string SignatureHeader = "X-WU-Signature";
    public const string FirewallRuleName = "Windows Utility remote monitoring";

    /// <summary>Second rule, only with "Also allow PCs on my VPN": opens the port to Tailscale addresses on any network.</summary>
    public const string VpnFirewallRuleName = "Windows Utility remote monitoring (VPN)";

    /// <summary>Tailscale's address ranges (IPv4 shared address space and its IPv6 prefix).</summary>
    public const string VpnRemoteAddresses = "100.64.0.0/10,fd7a:115c:a1e0::/48";

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

    /// <summary>Whether this PC accepts actions (restart, lock, message...) from the PC monitoring it.</summary>
    public bool ActionsAllowed { get; set; }

    /// <summary>Signed-in users and whether each is active, locked or disconnected.</summary>
    public List<RemoteSessionInfo> Sessions { get; set; } = new();

    /// <summary>Wired and Wi-Fi adapters with their addresses and whether they wake the PC (empty from PCs on 2.0.76 or older).</summary>
    public List<RemoteAdapterInfo> Adapters { get; set; } = new();

    /// <summary>Restore points and backups (null from PCs on 2.0.76 or older, or before the first check).</summary>
    public RemoteRecoveryInfo? Recovery { get; set; }

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

/// <summary>A physical network adapter, for Wake-on-LAN.</summary>
public sealed class RemoteAdapterInfo
{
    public string Name { get; set; } = string.Empty;

    /// <summary>"AA-BB-CC-DD-EE-FF".</summary>
    public string MacAddress { get; set; } = string.Empty;

    public bool IsWired { get; set; }
    public bool IsConnected { get; set; }

    /// <summary>"Wake on magic packet" in the adapter's power settings; null when Windows does not say.</summary>
    public bool? WakeOnMagicPacket { get; set; }
}

/// <summary>System restore and Windows backup state of a monitored PC.</summary>
public sealed class RemoteRecoveryInfo
{
    public DateTime CheckedUtc { get; set; }

    /// <summary>System Protection (restore points) on the Windows drive; null when it could not be read.</summary>
    public bool? SystemProtectionOn { get; set; }

    public DateTime? LastRestorePointUtc { get; set; }
    public string LastRestorePointDescription { get; set; } = string.Empty;
    public int RestorePointCount { get; set; }

    /// <summary>Last successful Windows Backup (wbadmin / Backup and Restore), from the event log.</summary>
    public DateTime? LastBackupUtc { get; set; }
}

/// <summary>One minute (or a longer stretch, when averaged) of a PC's readings.</summary>
public sealed class RemoteHistoryPoint
{
    public DateTime TimeUtc { get; set; }
    public double? CpuPercent { get; set; }

    /// <summary>Highest CPU temperature in the stretch.</summary>
    public double? TemperatureCelsius { get; set; }
    public double? RamPercent { get; set; }

    /// <summary>Busiest disk's active time.</summary>
    public double? DiskActivePercent { get; set; }
    public double? ReceiveBitsPerSec { get; set; }
    public double? SendBitsPerSec { get; set; }
}

public sealed class RemoteHistory
{
    public int Days { get; set; }

    /// <summary>Length of each point in minutes (5 for a day, 30 for a week).</summary>
    public int StepMinutes { get; set; }

    /// <summary>When the PC started recording (its oldest kept minute).</summary>
    public DateTime? RecordingSinceUtc { get; set; }
    public List<RemoteHistoryPoint> Points { get; set; } = new();
}

/// <summary>An installed program.</summary>
public sealed class RemoteProgram
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public DateTime? InstalledOn { get; set; }
    public long? SizeBytes { get; set; }

    /// <summary>"All users" or the user it is installed for.</summary>
    public string InstalledFor { get; set; } = string.Empty;
}

public sealed class RemoteSoftwareList
{
    public DateTime CollectedUtc { get; set; }
    public List<RemoteProgram> Programs { get; set; } = new();
}

/// <summary>What the phone page shows, written by the main PC's dashboard after each check of the monitored PCs.</summary>
public sealed class PhoneViewData
{
    public string MainPcName { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    public List<PhoneViewPc> Pcs { get; set; } = new();
}

public sealed class PhoneViewPc
{
    public string Name { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;

    /// <summary>Online, Warning, Offline or Checking (same as the tile's status pill).</summary>
    public string StatusKind { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public double? CpuPercent { get; set; }
    public string Temperature { get; set; } = string.Empty;
    public double? RamPercent { get; set; }
    public List<PhoneViewDisk> Disks { get; set; } = new();
    public string SignedIn { get; set; } = string.Empty;
    public List<string> Notes { get; set; } = new();
    public string LastSeen { get; set; } = string.Empty;
}

public sealed class PhoneViewDisk
{
    public string Name { get; set; } = string.Empty;
    public double UsedPercent { get; set; }
    public string Free { get; set; } = string.Empty;
    public bool Low { get; set; }
}

/// <summary>An action for a monitored PC.</summary>
public sealed class RemoteActionRequest
{
    /// <summary>message, lock, restart, shutdown, cancel-shutdown, clear-temp, update-scan or restore-point.</summary>
    public string Action { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int DelayMinutes { get; set; }

    /// <summary>Unique per request; the PC refuses a request id it has already seen.</summary>
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Name of the PC that asked (for the log on both PCs).</summary>
    public string RequestedBy { get; set; } = string.Empty;
}

public sealed class RemoteActionResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
}

public static class RemoteActions
{
    public const string Message = "message";
    public const string Lock = "lock";
    public const string Restart = "restart";
    public const string Shutdown = "shutdown";
    public const string CancelShutdown = "cancel-shutdown";
    public const string ClearTemp = "clear-temp";
    public const string UpdateScan = "update-scan";
    public const string RestorePoint = "restore-point";

    public static readonly string[] All = { Message, Lock, Restart, Shutdown, CancelShutdown, ClearTemp, UpdateScan, RestorePoint };
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

    /// <summary>Group shown on the tile and used to filter, e.g. "Office" or "Laptops"; empty for none.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Adapter addresses last reported by the PC, used to wake it when it is off or asleep.</summary>
    public List<string> WakeMacAddresses { get; set; } = new();

    /// <summary>Whether a wired adapter reported "Wake on magic packet" turned on.</summary>
    public bool? WakeOnLanEnabled { get; set; }

    /// <summary>Last IPv4 address the PC was reached at (when added by name), for a directed wake packet.</summary>
    public string LastKnownAddress { get; set; } = string.Empty;
}

public static class VpnAddresses
{
    /// <summary>
    /// Tailscale addresses (100.64.0.0/10, fd7a:115c:a1e0::/48) and MagicDNS names (*.ts.net): such PCs are reached
    /// over the VPN, so they get longer timeouts and a "via VPN" label.
    /// </summary>
    public static bool IsVpnHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        host = host.Trim().TrimEnd('.');
        if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out System.Net.IPAddress? address))
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? bytes[0] == 100 && (bytes[1] & 0xC0) == 64
            : bytes.Length == 16 && bytes[0] == 0xFD && bytes[1] == 0x7A && bytes[2] == 0x11 && bytes[3] == 0x5C && bytes[4] == 0xA1 && bytes[5] == 0xE0;
    }
}
