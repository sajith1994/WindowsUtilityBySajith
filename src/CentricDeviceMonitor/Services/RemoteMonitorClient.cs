using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

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

    /// <summary>Remote clock minus local clock, per host:port, in seconds.</summary>
    private readonly ConcurrentDictionary<string, long> _clockOffsets = new(StringComparer.OrdinalIgnoreCase);

    public async Task<RemoteHello?> HelloAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await Http.GetAsync(BuildUri(host, port, RemoteMonitoringProtocol.HelloPath), cancellationToken);
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
                using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
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

    private static string Key(string host, int port) => $"{host}:{port}";

    private static Uri BuildUri(string host, int port, string path)
    {
        string authority = IPAddress.TryParse(host, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;
        return new Uri($"http://{authority}:{port}{path}");
    }
}
