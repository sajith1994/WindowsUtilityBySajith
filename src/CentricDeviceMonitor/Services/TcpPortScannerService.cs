using System.Net;
using System.Net.Sockets;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed class TcpPortScannerService
{
    private static readonly (int Port, string Service)[] CommonTcpPorts =
    {
        (20, "FTP-data"), (21, "FTP"), (22, "SSH"), (23, "Telnet"), (25, "SMTP"),
        (53, "DNS"), (80, "HTTP"), (110, "POP3"), (135, "RPC"), (139, "NetBIOS"),
        (143, "IMAP"), (389, "LDAP"), (443, "HTTPS"), (445, "SMB"), (465, "SMTPS"),
        (587, "SMTP submission"), (636, "LDAPS"), (993, "IMAPS"), (995, "POP3S"),
        (1433, "SQL Server"), (1521, "Oracle"), (2049, "NFS"), (3306, "MySQL"),
        (3389, "RDP"), (5432, "PostgreSQL"), (5900, "VNC"), (5985, "WinRM HTTP"),
        (5986, "WinRM HTTPS"), (8080, "HTTP alt"), (8443, "HTTPS alt"), (9100, "Printer")
    };

    public int PortCount => CommonTcpPorts.Length;

    public async Task<IReadOnlyList<TcpPortScanResult>> ScanCommonPortsAsync(
        string ipAddress,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(ipAddress, out IPAddress? address) ||
            (address.AddressFamily != AddressFamily.InterNetwork && address.AddressFamily != AddressFamily.InterNetworkV6))
        {
            throw new ArgumentException("A valid IP address is required.", nameof(ipAddress));
        }

        List<TcpPortScanResult> openPorts = new();
        object resultLock = new();
        using SemaphoreSlim limiter = new(16, 16);

        IEnumerable<Task> tasks = CommonTcpPorts.Select(async item =>
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await IsTcpPortOpenAsync(address, item.Port, cancellationToken).ConfigureAwait(false))
                {
                    lock (resultLock)
                    {
                        openPorts.Add(new TcpPortScanResult(item.Port, item.Service));
                    }
                }
            }
            finally
            {
                limiter.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return openPorts.OrderBy(result => result.Port).ToList();
    }

    private static async Task<bool> IsTcpPortOpenAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using TcpClient client = new(address.AddressFamily);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(450));
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}
