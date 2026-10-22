using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace CentricDeviceMonitor.Services;

public sealed record NetworkCheckResult(
    bool IsOnline,
    string Hostname,
    string MacAddress,
    long? RoundTripTime,
    string ErrorMessage);

public sealed class NetworkDeviceService
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(
        uint destinationIp,
        uint sourceIp,
        byte[] macAddress,
        ref int physicalAddressLength);

    public async Task<NetworkCheckResult> CheckAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(ipAddress, out IPAddress? parsedAddress) ||
            parsedAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return new NetworkCheckResult(false, "Not available", "Not available", null, "Invalid IPv4 address");
        }

        string hostname = await ResolveHostnameAsync(parsedAddress, cancellationToken).ConfigureAwait(false);
        string macAddress = ResolveMacAddress(parsedAddress);

        try
        {
            // A second, longer ping catches devices that were slow to answer the first (Wi-Fi power saving).
            IPStatus status = IPStatus.Unknown;
            foreach (int timeout in new[] { 1500, 2500 })
            {
                using Ping ping = new();
                PingReply reply = await ping.SendPingAsync(parsedAddress, timeout)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);

                if (reply.Status == IPStatus.Success)
                {
                    return new NetworkCheckResult(true, hostname, macAddress, reply.RoundtripTime, string.Empty);
                }

                status = reply.Status;
            }

            // Some devices block ping but still answer (or refuse) a connection, which proves they are on.
            if (await AnswersOnAnyPortAsync(parsedAddress, cancellationToken).ConfigureAwait(false) is int port)
            {
                return new NetworkCheckResult(true, hostname, ResolveMacAddress(parsedAddress), null, $"Does not answer ping; answered on port {port}.");
            }

            return new NetworkCheckResult(false, hostname, macAddress, null, $"Ping status: {status}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new NetworkCheckResult(false, hostname, macAddress, null, exception.Message);
        }
    }

    /// <summary>Ports that phones, PCs, printers, NAS boxes and media players commonly answer on.</summary>
    private static readonly int[] ProbePorts = { 80, 443, 445, 139, 22, 3389, 9100, 5000, 8009, 62078 };

    /// <summary>The first port where the device accepted or refused a connection (either means it is on), or null.</summary>
    private static async Task<int?> AnswersOnAnyPortAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(900));
        Task<int?>[] probes = ProbePorts.Select(async port =>
        {
            using System.Net.Sockets.TcpClient client = new();
            try
            {
                await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
                return (int?)port;
            }
            catch (System.Net.Sockets.SocketException exception) when (exception.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
            {
                return port;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }).ToArray();

        int?[] answers = await Task.WhenAll(probes).ConfigureAwait(false);
        return answers.FirstOrDefault(port => port.HasValue);
    }


    public string ResolveLocalMacAddress(string ipAddress)
    {
        return IPAddress.TryParse(ipAddress, out IPAddress? parsedAddress) &&
               parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? ResolveMacAddress(parsedAddress)
            : "Not available";
    }

    public async Task<string> ResolveHostnameAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        return IPAddress.TryParse(ipAddress, out IPAddress? parsedAddress) &&
               parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? await ResolveHostnameAsync(parsedAddress, cancellationToken).ConfigureAwait(false)
            : "Not available";
    }

    private static async Task<string> ResolveHostnameAsync(IPAddress ipAddress, CancellationToken cancellationToken)
    {
        try
        {
            IPHostEntry entry = await Dns.GetHostEntryAsync(ipAddress)
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(entry.HostName) ? "Not available" : entry.HostName;
        }
        catch
        {
            return "Not available";
        }
    }

    private static string ResolveMacAddress(IPAddress ipAddress)
    {
        try
        {
            byte[] addressBytes = ipAddress.GetAddressBytes();
            uint destinationIp = BitConverter.ToUInt32(addressBytes, 0);
            byte[] macBytes = new byte[6];
            int length = macBytes.Length;

            int result = SendARP(destinationIp, 0, macBytes, ref length);
            if (result != 0 || length <= 0)
            {
                return "Not available";
            }

            return string.Join("-", macBytes.Take(length).Select(value => value.ToString("X2")));
        }
        catch
        {
            return "Not available";
        }
    }
}
