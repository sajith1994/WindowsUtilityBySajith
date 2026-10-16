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
    public static readonly string[] SignedPaths = { StatsPath, SpeedDownloadPath, SpeedUploadPath, InternetSpeedPath };
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
}

public sealed class RemoteDiskInfo
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
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
