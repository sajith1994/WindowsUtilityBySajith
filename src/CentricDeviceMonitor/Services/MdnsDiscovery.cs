using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace CentricDeviceMonitor.Services;

/// <summary>What one device announced over mDNS (Bonjour / Avahi).</summary>
public sealed class MdnsDevice
{
    public string Address { get; init; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public HashSet<string> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> InstanceNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One-shot mDNS service discovery. Sends PTR questions for file-sharing and device services from every local
/// IPv4 address and collects the answers for a few seconds. The questions come from an ordinary (non-5353) port,
/// so responders answer directly to it ("legacy unicast", RFC 6762 section 6.7) and no multicast group is joined.
/// NAS boxes (Synology, QNAP, TrueNAS, Unraid, WD, Asustor and others) announce themselves this way even when they
/// ignore ping.
/// </summary>
public static class MdnsDiscovery
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static readonly string[] FileServices =
    {
        "_services._dns-sd._udp.local", "_smb._tcp.local", "_afpovertcp._tcp.local", "_nfs._tcp.local", "_adisk._tcp.local",
        "_device-info._tcp.local", "_http._tcp.local", "_https._tcp.local", "_qdiscover._tcp.local", "_webdav._tcp.local"
    };

    public static async Task<IReadOnlyDictionary<string, MdnsDevice>> DiscoverAsync(TimeSpan listenFor, CancellationToken cancellationToken = default, IEnumerable<string>? services = null)
    {
        Dictionary<string, MdnsDevice> devices = new(StringComparer.OrdinalIgnoreCase);
        byte[] query = BuildQuery(services ?? FileServices);
        List<UdpClient> clients = new();
        try
        {
            foreach (IPAddress local in LocalIpv4Addresses())
            {
                try
                {
                    UdpClient client = new(new IPEndPoint(local, 0));
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    await client.SendAsync(query, query.Length, MulticastEndpoint);
                    clients.Add(client);
                }
                catch (SocketException)
                {
                    // That adapter cannot send multicast; the others still can.
                }
            }

            using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(listenFor);
            Task listening = Task.WhenAll(clients.Select(client => ReceiveAsync(client, devices, window.Token)));

            // Responders skip or lose answers now and then (UDP, sleeping radios); asking again a second later
            // catches most of them.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), window.Token);
                foreach (UdpClient client in clients)
                {
                    await client.SendAsync(query, query.Length, MulticastEndpoint);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }

            await listening;
        }
        finally
        {
            foreach (UdpClient client in clients)
            {
                client.Dispose();
            }
        }

        return devices;
    }

    private static async Task ReceiveAsync(UdpClient client, Dictionary<string, MdnsDevice> devices, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await client.ReceiveAsync(token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            string address = received.RemoteEndPoint.Address.MapToIPv4().ToString();
            lock (devices)
            {
                if (!devices.TryGetValue(address, out MdnsDevice? device))
                {
                    device = new MdnsDevice { Address = address };
                    devices[address] = device;
                }

                try
                {
                    Parse(received.Buffer, device);
                }
                catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentException or DecoderFallbackException)
                {
                    // A malformed or truncated answer: keep what was read.
                }
            }
        }
    }

    private static IEnumerable<IPAddress> LocalIpv4Addresses()
    {
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || SystemResourceMonitorService.IsVirtualAdapter(nic))
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork
                    && !unicast.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    yield return unicast.Address;
                }
            }
        }
    }

    private static byte[] BuildQuery(IEnumerable<string> names)
    {
        List<byte> packet = new() { 0, 0, 0, 0 }; // ID 0, standard query
        string[] list = names.ToArray();
        packet.AddRange(new byte[] { (byte)(list.Length >> 8), (byte)list.Length, 0, 0, 0, 0, 0, 0 });
        foreach (string name in list)
        {
            foreach (string label in name.Split('.'))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(label);
                packet.Add((byte)bytes.Length);
                packet.AddRange(bytes);
            }

            packet.Add(0);
            packet.AddRange(new byte[] { 0, 12, 0x80, 1 }); // PTR, class IN with the "unicast response" bit
        }

        return packet.ToArray();
    }

    private static void Parse(byte[] data, MdnsDevice device)
    {
        if (data.Length < 12)
        {
            return;
        }

        int questions = data[4] << 8 | data[5];
        int records = (data[6] << 8 | data[7]) + (data[8] << 8 | data[9]) + (data[10] << 8 | data[11]);
        int offset = 12;
        for (int index = 0; index < questions; index++)
        {
            ReadName(data, ref offset);
            offset += 4;
        }

        for (int index = 0; index < records && offset < data.Length; index++)
        {
            string owner = ReadName(data, ref offset);
            int type = data[offset] << 8 | data[offset + 1];
            int length = data[offset + 8] << 8 | data[offset + 9];
            offset += 10;
            int rdata = offset;
            offset += length;

            switch (type)
            {
                case 1 when length == 4: // A
                    if (owner.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(device.HostName))
                    {
                        device.HostName = owner[..^".local".Length];
                    }

                    break;
                case 12: // PTR: service type -> instance name
                {
                    int position = rdata;
                    string instance = ReadName(data, ref position);
                    if (owner.StartsWith('_'))
                    {
                        device.Services.Add(owner);
                        string service = "." + owner;
                        if (instance.EndsWith(service, StringComparison.OrdinalIgnoreCase))
                        {
                            device.InstanceNames.Add(instance[..^service.Length]);
                        }
                    }

                    break;
                }
                case 33: // SRV: target host
                {
                    int position = rdata + 6;
                    string target = ReadName(data, ref position);
                    if (target.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(device.HostName))
                    {
                        device.HostName = target[..^".local".Length];
                    }

                    break;
                }
                case 16: // TXT: key=value strings
                {
                    int position = rdata;
                    while (position < rdata + length)
                    {
                        int size = data[position++];
                        if (size == 0 || position + size > data.Length)
                        {
                            break;
                        }

                        string entry = Encoding.UTF8.GetString(data, position, size);
                        position += size;
                        int equals = entry.IndexOf('=');
                        if (equals > 0)
                        {
                            device.Txt.TryAdd(entry[..equals], entry[(equals + 1)..]);
                        }
                    }

                    break;
                }
            }
        }
    }

    private static string ReadName(byte[] data, ref int offset)
    {
        StringBuilder name = new();
        int position = offset;
        bool jumped = false;
        int guard = 0;
        while (guard++ < 128)
        {
            int length = data[position];
            if (length == 0)
            {
                position++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                int pointer = (length & 0x3F) << 8 | data[position + 1];
                if (!jumped)
                {
                    offset = position + 2;
                }

                jumped = true;
                position = pointer;
                continue;
            }

            if (name.Length > 0)
            {
                name.Append('.');
            }

            name.Append(Encoding.UTF8.GetString(data, position + 1, length));
            position += length + 1;
        }

        if (!jumped)
        {
            offset = position;
        }

        return name.ToString();
    }
}
