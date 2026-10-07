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
            using Ping ping = new();
            PingReply reply = await ping.SendPingAsync(parsedAddress, 1500)
                .WaitAsync(cancellationToken).ConfigureAwait(false);

            if (reply.Status == IPStatus.Success)
            {
                return new NetworkCheckResult(true, hostname, macAddress, reply.RoundtripTime, string.Empty);
            }

            return new NetworkCheckResult(false, hostname, macAddress, null, $"Ping status: {reply.Status}");
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
