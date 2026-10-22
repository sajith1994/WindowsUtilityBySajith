using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using WindowsUtility.Models;

namespace WindowsUtility.Services;

public enum RemoteStatsState
{
    Online,
    WrongCode,
    NotSharing,
    Unreachable,
    Error
}

public sealed record RemoteStatsResult(RemoteStatsState State, RemoteStatsPayload? Stats, string Message);

public sealed record DiscoveredRemotePc(string Address, RemoteHello Hello);

/// <summary>
/// Reads stats from PCs that allow remote monitoring. Requests are signed with the PC's pairing code using that
/// PC's clock (learned from its hello answer), so PCs whose clocks disagree still work.
/// </summary>
public sealed class RemoteMonitorClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>PCs reached over a VPN answer more slowly, so they get longer.</summary>
    private static readonly HttpClient VpnHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static HttpClient HttpFor(string host) => VpnAddresses.IsVpnHost(host) ? VpnHttp : Http;

    /// <summary>Remote clock minus local clock, per host:port, in seconds.</summary>
    private readonly ConcurrentDictionary<string, long> _clockOffsets = new(StringComparer.OrdinalIgnoreCase);

    public async Task<RemoteHello?> HelloAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await HttpFor(host).GetAsync(BuildUri(host, port, RemoteMonitoringProtocol.HelloPath), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        RemoteHello? hello = JsonSerializer.Deserialize<RemoteHello>(await response.Content.ReadAsStringAsync(cancellationToken), JsonOptions);
        if (hello is not null && hello.ServerTimeUnix > 0)
        {
            _clockOffsets[Key(host, port)] = hello.ServerTimeUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        return hello;
    }

    public async Task<RemoteStatsResult> GetStatsAsync(string host, int port, string pairingCode, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_clockOffsets.ContainsKey(Key(host, port)) && await HelloAsync(host, port, cancellationToken) is null)
            {
                return new RemoteStatsResult(RemoteStatsState.NotSharing, null, "Answered, but it is not sharing its stats.");
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _clockOffsets.GetValueOrDefault(Key(host, port));
                using HttpRequestMessage request = new(HttpMethod.Get, BuildUri(host, port, RemoteMonitoringProtocol.StatsPath));
                request.Headers.Add(RemoteMonitoringProtocol.TimeHeader, time.ToString());
                request.Headers.Add(RemoteMonitoringProtocol.SignatureHeader, RemoteMonitoringProtocol.Sign(pairingCode, time, RemoteMonitoringProtocol.StatsPath));
                using HttpResponseMessage response = await HttpFor(host).SendAsync(request, cancellationToken);
                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        RemoteStatsPayload? stats = JsonSerializer.Deserialize<RemoteStatsPayload>(
                            await response.Content.ReadAsStringAsync(cancellationToken), JsonOptions);
                        return stats is null
                            ? new RemoteStatsResult(RemoteStatsState.Error, null, "The PC sent an empty answer.")
                            : new RemoteStatsResult(RemoteStatsState.Online, stats, "Online");
                    case HttpStatusCode.Unauthorized when attempt == 0:
                        // The PC's clock may have moved; learn it again and retry once before blaming the code.
                        await HelloAsync(host, port, cancellationToken);
                        continue;
                    case HttpStatusCode.Unauthorized:
                        return new RemoteStatsResult(RemoteStatsState.WrongCode, null, "Wrong pairing code. Check the code shown on that PC.");
                    case HttpStatusCode.TooManyRequests:
                        return new RemoteStatsResult(RemoteStatsState.WrongCode, null, "Too many wrong codes. That PC is ignoring this PC for a minute.");
                    case HttpStatusCode.NotFound:
                        return new RemoteStatsResult(RemoteStatsState.NotSharing, null, "Answered, but it does not support remote monitoring. Update Windows Utility on it.");
                    case HttpStatusCode.Forbidden:
                        // Its port is open for its phone page, but sharing its own stats is off.
                        return new RemoteStatsResult(RemoteStatsState.NotSharing, null, "Answered, but it is not sharing its stats.");
                    default:
                        return new RemoteStatsResult(RemoteStatsState.Error, null, $"The PC answered with HTTP {(int)response.StatusCode}.");
                }
            }

            return new RemoteStatsResult(RemoteStatsState.WrongCode, null, "Wrong pairing code. Check the code shown on that PC.");
        }
        catch (HttpRequestException exception) when (exception.InnerException is SocketException socket)
        {
            _clockOffsets.TryRemove(Key(host, port), out _);
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => new RemoteStatsResult(RemoteStatsState.NotSharing, null,
                    "The PC is on, but sharing is off (or the Windows Utility service is not running)."),
                SocketError.HostNotFound or SocketError.NoData => new RemoteStatsResult(RemoteStatsState.Unreachable, null,
                    $"{host} could not be found on the network."),
                _ => new RemoteStatsResult(RemoteStatsState.Unreachable, null, "Offline or unreachable (off, asleep, or blocked by a firewall).")
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _clockOffsets.TryRemove(Key(host, port), out _);
            return new RemoteStatsResult(RemoteStatsState.Unreachable, null, "Offline or unreachable (off, asleep, or blocked by a firewall).");
        }
        catch (JsonException)
        {
            return new RemoteStatsResult(RemoteStatsState.Error, null, "The PC sent an answer this version cannot read.");
        }
    }

    // ---- Speed tests ---------------------------------------------------------------------------------------

    private static readonly HttpClient LongHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Median round trip of five hello requests, in milliseconds.</summary>
    public async Task<double> MeasureLatencyAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        List<double> samples = new();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            if (await HelloAsync(host, port, cancellationToken) is null)
            {
                throw new InvalidOperationException("That PC answered, but it is not sharing its stats.");
            }

            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }

    /// <summary>Megabits per second while reading test data from the PC for <paramref name="duration"/>.</summary>
    public async Task<double> MeasureLanDownloadAsync(RemotePcEntry pc, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        await EnsureClockAsync(pc, cancellationToken);
        using HttpRequestMessage request = Signed(HttpMethod.Get, pc, RemoteMonitoringProtocol.SpeedDownloadPath, "?mb=1024");
        using HttpResponseMessage response = await LongHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        ThrowIfFailed(response);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[256 * 1024];
        long total = 0;
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        int read;
        while (watch.Elapsed < duration && (read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
        }

        return total * 8 / Math.Max(0.001, watch.Elapsed.TotalSeconds) / 1_000_000;
    }

    /// <summary>Megabits per second while sending test data to the PC for <paramref name="duration"/> (timed by that PC).</summary>
    public async Task<double> MeasureLanUploadAsync(RemotePcEntry pc, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        await EnsureClockAsync(pc, cancellationToken);
        using HttpRequestMessage request = Signed(HttpMethod.Post, pc, RemoteMonitoringProtocol.SpeedUploadPath, string.Empty);
        request.Content = new TimedUploadContent(duration);
        using HttpResponseMessage response = await LongHttp.SendAsync(request, cancellationToken);
        ThrowIfFailed(response);
        RemoteUploadResult? result = JsonSerializer.Deserialize<RemoteUploadResult>(await response.Content.ReadAsStringAsync(cancellationToken), JsonOptions);
        if (result is null || result.Bytes == 0)
        {
            throw new InvalidOperationException("That PC did not receive any test data.");
        }

        return result.Bytes * 8 / Math.Max(0.001, result.Milliseconds / 1000.0) / 1_000_000;
    }

    /// <summary>Asks the PC to measure its own internet speed (takes about 15 seconds).</summary>
    public async Task<RemoteInternetSpeedResult> RunInternetSpeedTestAsync(RemotePcEntry pc, CancellationToken cancellationToken = default)
    {
        await EnsureClockAsync(pc, cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using HttpRequestMessage request = Signed(HttpMethod.Get, pc, RemoteMonitoringProtocol.InternetSpeedPath, string.Empty);
        using HttpResponseMessage response = await LongHttp.SendAsync(request, timeout.Token);
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            ThrowIfFailed(response);
        }

        return JsonSerializer.Deserialize<RemoteInternetSpeedResult>(await response.Content.ReadAsStringAsync(timeout.Token), JsonOptions)
            ?? new RemoteInternetSpeedResult { Error = "That PC sent an empty answer." };
    }

    /// <summary>Runs an action on the PC (it must allow actions). The signature covers the whole request.</summary>
    public async Task<RemoteActionResult> RunActionAsync(RemotePcEntry pc, RemoteActionRequest action, CancellationToken cancellationToken = default)
    {
        await EnsureClockAsync(pc, cancellationToken);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(action);
        long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _clockOffsets.GetValueOrDefault(Key(pc.Host, pc.Port));
        using HttpRequestMessage request = new(HttpMethod.Post, BuildUri(pc.Host, pc.Port, RemoteMonitoringProtocol.ActionPath));
        request.Headers.Add(RemoteMonitoringProtocol.TimeHeader, time.ToString());
        request.Headers.Add(RemoteMonitoringProtocol.SignatureHeader, RemoteMonitoringProtocol.Sign(pc.PairingCode, time, RemoteMonitoringProtocol.ActionSignedText(body)));
        request.Content = new ByteArrayContent(body);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using HttpResponseMessage response = await LongHttp.SendAsync(request, timeout.Token);
        string text = await response.Content.ReadAsStringAsync(timeout.Token);
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed:
                throw new InvalidOperationException("That PC runs an older Windows Utility without actions. Update it to 2.0.75 or later (Update on its tile).");
            case HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.BadRequest or HttpStatusCode.OK:
                return JsonSerializer.Deserialize<RemoteActionResult>(text, JsonOptions)
                    ?? new RemoteActionResult { Ok = false, Message = "That PC sent an empty answer." };
            default:
                ThrowIfFailed(response);
                return new RemoteActionResult { Ok = false, Message = $"That PC answered with HTTP {(int)response.StatusCode}." };
        }
    }
    /// <summary>Programs using the PC now, signed-in users, last restart and the last 30 days of problems.</summary>
    public async Task<RemotePcDetails> GetDetailsAsync(RemotePcEntry pc, CancellationToken cancellationToken = default)
    {
        await EnsureClockAsync(pc, cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpRequestMessage request = Signed(HttpMethod.Get, pc, RemoteMonitoringProtocol.DetailsPath, string.Empty);
        using HttpResponseMessage response = await LongHttp.SendAsync(request, timeout.Token);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("That PC runs an older Windows Utility without the Details view. Update it to 2.0.74 or later (Update on its tile).");
        }

        ThrowIfFailed(response);
        return JsonSerializer.Deserialize<RemotePcDetails>(await response.Content.ReadAsStringAsync(timeout.Token), JsonOptions)
            ?? throw new InvalidOperationException("That PC sent an empty answer.");
    }
    /// <summary>The PC's recorded readings for the last day (5-minute points) or week (30-minute points).</summary>
    public async Task<RemoteHistory> GetHistoryAsync(RemotePcEntry pc, int days, CancellationToken cancellationToken = default)
    {
        await EnsureClockAsync(pc, cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using HttpRequestMessage request = Signed(HttpMethod.Get, pc, RemoteMonitoringProtocol.HistoryPath, $"?days={days}");
        using HttpResponseMessage response = await LongHttp.SendAsync(request, timeout.Token);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("That PC runs an older Windows Utility that does not record history. Update it to 2.0.77 or later (Update on its tile); it starts recording then.");
        }

        ThrowIfFailed(response);
        return JsonSerializer.Deserialize<RemoteHistory>(await response.Content.ReadAsStringAsync(timeout.Token), JsonOptions)
            ?? throw new InvalidOperationException("That PC sent an empty answer.");
    }

    /// <summary>Programs installed on the PC.</summary>
    public async Task<RemoteSoftwareList> GetSoftwareAsync(RemotePcEntry pc, CancellationToken cancellationToken = default)
    {
        await EnsureClockAsync(pc, cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using HttpRequestMessage request = Signed(HttpMethod.Get, pc, RemoteMonitoringProtocol.SoftwarePath, string.Empty);
        using HttpResponseMessage response = await LongHttp.SendAsync(request, timeout.Token);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("That PC runs an older Windows Utility without the software list. Update it to 2.0.77 or later (Update on its tile).");
        }

        ThrowIfFailed(response);
        return JsonSerializer.Deserialize<RemoteSoftwareList>(await response.Content.ReadAsStringAsync(timeout.Token), JsonOptions)
            ?? throw new InvalidOperationException("That PC sent an empty answer.");
    }

    /// <summary>Asks the PC to update itself to the latest official release.</summary>
    public async Task<RemoteUpdateResult> RequestUpdateAsync(RemotePcEntry pc, CancellationToken cancellationToken = default)
    {
        await EnsureClockAsync(pc, cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using HttpRequestMessage request = Signed(HttpMethod.Post, pc, RemoteMonitoringProtocol.UpdatePath, string.Empty);
        request.Content = new ByteArrayContent(Array.Empty<byte>());
        using HttpResponseMessage response = await LongHttp.SendAsync(request, timeout.Token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            throw new InvalidOperationException("That PC runs an older Windows Utility that cannot be updated from here yet. Update it once on that PC (or let its automatic updates do it); from 2.0.67 on it can be updated remotely.");
        }

        ThrowIfFailed(response);
        return JsonSerializer.Deserialize<RemoteUpdateResult>(await response.Content.ReadAsStringAsync(timeout.Token), JsonOptions)
            ?? new RemoteUpdateResult { Status = "Failed", Message = "That PC sent an empty answer." };
    }
    private async Task EnsureClockAsync(RemotePcEntry pc, CancellationToken cancellationToken)
    {
        if (!_clockOffsets.ContainsKey(Key(pc.Host, pc.Port)) && await HelloAsync(pc.Host, pc.Port, cancellationToken) is null)
        {
            throw new InvalidOperationException("That PC answered, but it is not sharing its stats.");
        }
    }

    private HttpRequestMessage Signed(HttpMethod method, RemotePcEntry pc, string path, string query)
    {
        long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _clockOffsets.GetValueOrDefault(Key(pc.Host, pc.Port));
        HttpRequestMessage request = new(method, BuildUri(pc.Host, pc.Port, path + query));
        request.Headers.Add(RemoteMonitoringProtocol.TimeHeader, time.ToString());
        request.Headers.Add(RemoteMonitoringProtocol.SignatureHeader, RemoteMonitoringProtocol.Sign(pc.PairingCode, time, path));
        return request;
    }

    private static void ThrowIfFailed(HttpResponseMessage response)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return;
            case HttpStatusCode.Unauthorized:
                throw new InvalidOperationException("Wrong pairing code. Edit this PC and enter the code shown on it.");
            case HttpStatusCode.TooManyRequests:
                throw new InvalidOperationException("Too many wrong codes. That PC is ignoring this PC for a minute.");
            case HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed:
                throw new InvalidOperationException("That PC runs an older Windows Utility without speed tests. Update it to 2.0.65 or later.");
            default:
                throw new InvalidOperationException($"That PC answered with HTTP {(int)response.StatusCode}.");
        }
    }

    /// <summary>Request body that keeps sending random 1 MB blocks until the time is up (chunked, no length).</summary>
    private sealed class TimedUploadContent : HttpContent
    {
        private static readonly byte[] Block = CreateBlock();
        private readonly TimeSpan _duration;

        public TimedUploadContent(TimeSpan duration) => _duration = duration;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < _duration)
            {
                await stream.WriteAsync(Block);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private static byte[] CreateBlock()
        {
            byte[] block = new byte[1024 * 1024];
            Random.Shared.NextBytes(block);
            return block;
        }
    }

    /// <summary>PCs on this subnet that answer as Windows Utility with sharing turned on.</summary>
    public async Task<IReadOnlyList<DiscoveredRemotePc>> DiscoverAsync(int port, CancellationToken cancellationToken = default)
    {
        LocalSubnetInfo subnet = new LocalIpScannerService().GetLocalSubnet()
            ?? throw new InvalidOperationException("No active Ethernet or Wi-Fi network was found to search.");

        ConcurrentBag<DiscoveredRemotePc> found = new();
        await Parallel.ForEachAsync(subnet.Addresses, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = cancellationToken },
            async (address, token) =>
            {
                using (TcpClient probe = new())
                {
                    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
                    try
                    {
                        await probe.ConnectAsync(address, port, timeout.Token);
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    {
                        return;
                    }
                }

                try
                {
                    RemoteHello? hello = await HelloAsync(address, port, token);
                    if (hello is not null)
                    {
                        found.Add(new DiscoveredRemotePc(address, hello));
                    }
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // Something else listens on that port.
                }
            });

        return found.OrderBy(pc => pc.Hello.MachineName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// PCs on this PC's Tailscale network that answer as Windows Utility, read from "tailscale status --json". Empty
    /// when Tailscale is not installed or not signed in.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredRemotePc>> DiscoverVpnAsync(int port, CancellationToken cancellationToken = default)
    {
        List<string> peers = await TailscalePeersAsync(cancellationToken);
        ConcurrentBag<DiscoveredRemotePc> found = new();
        await Parallel.ForEachAsync(peers, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
            async (address, token) =>
            {
                try
                {
                    if (await HelloAsync(address, port, token) is { } hello)
                    {
                        found.Add(new DiscoveredRemotePc(address, hello));
                    }
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // Not running Windows Utility with sharing (or VPN access) turned on.
                }
            });

        return found.OrderBy(pc => pc.Hello.MachineName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The Tailscale CLI, when Tailscale is installed.</summary>
    public static string? TailscaleExecutable()
    {
        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string path = Path.Combine(Environment.GetFolderPath(folder), "Tailscale", "tailscale.exe");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>IPv4 addresses of the online peers in "tailscale status --json".</summary>
    private static async Task<List<string>> TailscalePeersAsync(CancellationToken cancellationToken)
    {
        if (TailscaleExecutable() is not { } executable)
        {
            return new List<string>();
        }

        try
        {
            using System.Diagnostics.Process process = new()
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(executable, "status --json")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            return ParseTailscalePeers(await output);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new List<string>();
        }
    }

    internal static List<string> ParseTailscalePeers(string json)
    {
        List<string> addresses = new();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Peer", out JsonElement peers) || peers.ValueKind != JsonValueKind.Object)
            {
                return addresses;
            }

            foreach (JsonProperty peer in peers.EnumerateObject())
            {
                if (peer.Value.TryGetProperty("Online", out JsonElement online) && online.ValueKind == JsonValueKind.False)
                {
                    continue;
                }

                if (peer.Value.TryGetProperty("TailscaleIPs", out JsonElement ips) && ips.ValueKind == JsonValueKind.Array)
                {
                    string? ipv4 = ips.EnumerateArray().Select(ip => ip.GetString()).FirstOrDefault(ip => ip is not null && ip.Contains('.'));
                    if (ipv4 is not null)
                    {
                        addresses.Add(ipv4);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return addresses;
    }

    private static string Key(string host, int port) => $"{host}:{port}";

    private static Uri BuildUri(string host, int port, string path)
    {
        string authority = IPAddress.TryParse(host, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;
        return new Uri($"http://{authority}:{port}{path}");
    }
}
