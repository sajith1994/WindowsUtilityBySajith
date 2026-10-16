using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CentricDeviceMonitor.Models;
using Microsoft.Win32;

namespace CentricDeviceMonitor.ServiceHost;

/// <summary>
/// Read-only stats endpoint for remote monitoring, run by the LocalSystem service so it works before anyone signs
/// in. GET /wu/v1/hello answers with the PC name and clock (no stats); GET /wu/v1/stats needs a request signed with
/// the pairing code. A Windows Firewall rule opens the port to the local subnet on private and domain networks only.
/// </summary>
internal sealed class RemoteStatsServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
    private const int MaxFailuresPerWindow = 10;

    private readonly Func<BackgroundServiceSnapshot?> _snapshotProvider;
    private readonly string _listenHost;
    private readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> _failures = new();
    private readonly string _osDisplay = ReadOsDisplay();
    private readonly string _appVersion = typeof(RemoteStatsServer).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private HttpListener? _listener;
    private int _port;
    private volatile string _code = string.Empty;
    private int? _firewallPort;
    private bool _firewallChecked;
    private DateTime _retryAfter = DateTime.MinValue;
    private readonly object _partitionLock = new();
    private HashSet<string>? _partitionDrives;
    private DateTime _partitionDrivesReadAt;

    /// <param name="listenHost">"+" (all addresses) in the service; tests use "localhost", which needs no administrator rights.</param>
    public RemoteStatsServer(Func<BackgroundServiceSnapshot?> snapshotProvider, string listenHost = "+")
    {
        _snapshotProvider = snapshotProvider;
        _listenHost = listenHost;
    }

    public bool IsActive => _listener?.IsListening == true;

    public string Status { get; private set; } = "Off";

    /// <summary>Brings the endpoint in line with the settings. Called on every monitoring-loop pass.</summary>
    public void Apply(bool enabled, int port, string code)
    {
        port = port is >= 1024 and <= 65535 ? port : RemoteMonitoringProtocol.DefaultPort;
        if (!enabled || string.IsNullOrEmpty(RemoteMonitoringProtocol.NormalizeCode(code)))
        {
            if (_listener is not null)
            {
                StopListener();
                ServiceLog.Write("Remote monitoring", "Sharing turned off.");
            }

            // Once per service start, or after sharing was on: make sure no rule is left open.
            if (_firewallPort is not null || !_firewallChecked)
            {
                RemoveFirewallRule();
                _firewallPort = null;
                _firewallChecked = true;
            }

            Status = "Off";
            return;
        }

        _code = code;
        if (IsActive && port == _port)
        {
            return;
        }

        if (DateTime.Now < _retryAfter && port == _port)
        {
            return;
        }

        StopListener();
        _port = port;
        try
        {
            HttpListener listener = new();
            listener.Prefixes.Add($"http://{_listenHost}:{port}/wu/");
            listener.Start();
            _listener = listener;
            _ = Task.Run(() => AcceptLoopAsync(listener));
            if (_firewallPort != port)
            {
                AddFirewallRule(port);
                _firewallPort = port;
                _firewallChecked = true;
            }

            Status = $"Sharing on port {port}";
            ServiceLog.Write("Remote monitoring", $"Sharing this PC's stats on TCP port {port}.");
        }
        catch (Exception exception) when (exception is HttpListenerException or InvalidOperationException)
        {
            StopListener();
            _retryAfter = DateTime.Now.AddMinutes(1);
            Status = $"Could not open port {port}: {exception.Message}";
            ServiceLog.WriteException("Remote monitoring start", exception);
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => Handle(context));
        }
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                Respond(context, 405, new { error = "method" });
                return;
            }

            if (path.Equals(RemoteMonitoringProtocol.HelloPath, StringComparison.OrdinalIgnoreCase))
            {
                Respond(context, 200, new RemoteHello
                {
                    MachineName = Environment.MachineName,
                    AppVersion = _appVersion,
                    ServerTimeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
                return;
            }

            if (!path.Equals(RemoteMonitoringProtocol.StatsPath, StringComparison.OrdinalIgnoreCase))
            {
                Respond(context, 404, new { error = "not-found" });
                return;
            }

            string caller = context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
            if (IsBlocked(caller))
            {
                Respond(context, 429, new { error = "too-many-attempts" });
                return;
            }

            if (!RemoteMonitoringProtocol.Verify(
                    _code,
                    context.Request.Headers[RemoteMonitoringProtocol.TimeHeader],
                    context.Request.Headers[RemoteMonitoringProtocol.SignatureHeader],
                    RemoteMonitoringProtocol.StatsPath,
                    DateTimeOffset.UtcNow))
            {
                RecordFailure(caller);
                Respond(context, 401, new { error = "unauthorized" });
                return;
            }

            _failures.TryRemove(caller, out _);
            Respond(context, 200, BuildPayload());
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("Remote monitoring request", exception);
            try
            {
                context.Response.Abort();
            }
            catch
            {
            }
        }
    }

    private RemoteStatsPayload BuildPayload()
    {
        BackgroundServiceSnapshot? snapshot = _snapshotProvider();
        RemoteStatsPayload payload = new()
        {
            MachineName = Environment.MachineName,
            OsDisplay = _osDisplay,
            AppVersion = _appVersion,
            UptimeSeconds = Environment.TickCount64 / 1000,
            CollectedAtUtc = DateTime.UtcNow,
            ReadingsFresh = snapshot is not null && DateTime.Now - snapshot.UpdatedAt < TimeSpan.FromSeconds(30)
        };

        if (snapshot is not null)
        {
            payload.CpuModel = snapshot.CpuModel;
            payload.CpuUsagePercent = snapshot.CpuUsagePercent;
            payload.CpuTemperatureCelsius = snapshot.CpuTemperatureCelsius;
            payload.CpuTemperatureIsCpuSpecific = snapshot.CpuIsCpuSpecific;
            payload.CpuTemperatureDisplay = snapshot.CpuDisplay;
            payload.CpuTemperatureAlertThresholdCelsius = snapshot.CpuTemperatureAlertThresholdCelsius;
            payload.CpuFanRpm = snapshot.CpuFanRpm;
            payload.CpuPackagePowerWatts = snapshot.CpuPackagePowerWatts;
            payload.GpuPowerWatts = snapshot.GpuPowerWatts;
            payload.RamUsagePercent = snapshot.RamUsagePercent;
            payload.TotalMemoryBytes = snapshot.TotalPhysicalMemoryBytes;
            payload.AvailableMemoryBytes = snapshot.AvailablePhysicalMemoryBytes;
            payload.NetworkConnected = snapshot.NetworkConnected;
            payload.NetworkSummary = snapshot.NetworkSummary;
        }

        HashSet<string>? partitionDrives = GetPartitionBackedDrives();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                // Cloud and virtual drives (Google Drive, some VPN or encryption tools) also report as fixed; only
                // list drives that sit on a disk partition.
                if (partitionDrives is not null && !partitionDrives.Contains(drive.Name.TrimEnd('\\')))
                {
                    continue;
                }

                payload.Disks.Add(new RemoteDiskInfo
                {
                    Name = drive.Name.TrimEnd('\\'),
                    Label = drive.VolumeLabel,
                    Format = drive.DriveFormat,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.TotalFreeSpace
                });
            }
            catch (IOException)
            {
                // A drive that disappears mid-read is skipped.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return payload;
    }

    /// <summary>Drive letters backed by a disk partition, refreshed at most once a minute; null if WMI fails.</summary>
    private HashSet<string>? GetPartitionBackedDrives()
    {
        lock (_partitionLock)
        {
            if (_partitionDrives is not null && DateTime.UtcNow - _partitionDrivesReadAt < TimeSpan.FromMinutes(1))
            {
                return _partitionDrives;
            }

            try
            {
                HashSet<string> drives = new(StringComparer.OrdinalIgnoreCase);
                using ManagementObjectSearcher searcher = new("SELECT Dependent FROM Win32_LogicalDiskToPartition");
                foreach (ManagementBaseObject link in searcher.Get())
                {
                    using (link)
                    {
                        Match match = Regex.Match(link["Dependent"] as string ?? string.Empty, "DeviceID=\"([^\"]+)\"");
                        if (match.Success)
                        {
                            drives.Add(match.Groups[1].Value);
                        }
                    }
                }

                _partitionDrives = drives.Count > 0 ? drives : null;
            }
            catch (ManagementException)
            {
                _partitionDrives = null;
            }

            _partitionDrivesReadAt = DateTime.UtcNow;
            return _partitionDrives;
        }
    }

    private static void Respond(HttpListenerContext context, int status, object body)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), JsonOptions);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    private bool IsBlocked(string caller) =>
        _failures.TryGetValue(caller, out (int Count, DateTime WindowStart) entry)
        && DateTime.UtcNow - entry.WindowStart < FailureWindow
        && entry.Count >= MaxFailuresPerWindow;

    private void RecordFailure(string caller)
    {
        DateTime now = DateTime.UtcNow;
        _failures.AddOrUpdate(caller,
            _ => (1, now),
            (_, entry) => now - entry.WindowStart >= FailureWindow ? (1, now) : (entry.Count + 1, entry.WindowStart));
    }

    private void StopListener()
    {
        HttpListener? listener = _listener;
        _listener = null;
        if (listener is null)
        {
            return;
        }

        try
        {
            listener.Stop();
            listener.Close();
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
        {
        }
    }

    private static void AddFirewallRule(int port)
    {
        RemoveFirewallRule();
        RunNetsh($"advfirewall firewall add rule name=\"{RemoteMonitoringProtocol.FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port} remoteip=localsubnet profile=private,domain");
    }

    private static void RemoveFirewallRule() =>
        RunNetsh($"advfirewall firewall delete rule name=\"{RemoteMonitoringProtocol.FirewallRuleName}\"");

    private static void RunNetsh(string arguments)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("netsh.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(15000);
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("Remote monitoring firewall rule", exception);
        }
    }

    private static string ReadOsDisplay()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = key?.GetValue("ProductName") as string ?? "Windows";
            string release = key?.GetValue("DisplayVersion") as string ?? string.Empty;
            string build = key?.GetValue("CurrentBuild") as string ?? string.Empty;
            // Windows 11 still reports "Windows 10" in ProductName; the build number tells them apart.
            if (int.TryParse(build, out int buildNumber) && buildNumber >= 22000)
            {
                product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
            }

            return string.Join(" ", new[] { product, release }.Where(part => part.Length > 0));
        }
        catch (Exception)
        {
            return "Windows";
        }
    }

    public void Dispose() => StopListener();
}
