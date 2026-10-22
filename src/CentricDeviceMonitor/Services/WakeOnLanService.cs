using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// Wake-on-LAN: sends the "magic packet" (6 bytes of FF, then the adapter address 16 times) to UDP ports 9 and 7.
/// Windows sends a plain broadcast out of one adapter only, so the packet goes out of every connected adapter, both as
/// a broadcast and to that adapter's subnet. It only works on the local network, for PCs whose adapter and firmware
/// allow it.
/// </summary>
public static class WakeOnLanService
{
    private static readonly int[] Ports = { 9, 7 };

    public static byte[] BuildMagicPacket(string macAddress)
    {
        byte[] mac = ParseMac(macAddress) ?? throw new ArgumentException($"\"{macAddress}\" is not an adapter address (like AA-BB-CC-DD-EE-FF).", nameof(macAddress));
        byte[] packet = new byte[6 + 16 * 6];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (int copy = 0; copy < 16; copy++)
        {
            Buffer.BlockCopy(mac, 0, packet, 6 + copy * 6, 6);
        }

        return packet;
    }

    /// <summary>"AA-BB-CC-DD-EE-FF", "aa:bb:cc:dd:ee:ff" or "AABBCCDDEEFF"; null when it is not 6 bytes.</summary>
    public static byte[]? ParseMac(string? text)
    {
        string hex = new((text ?? string.Empty).Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12 || (text ?? string.Empty).Any(character => !Uri.IsHexDigit(character) && character is not ('-' or ':' or '.' or ' ')))
        {
            return null;
        }

        return Convert.FromHexString(hex);
    }

    /// <summary>Sends wake packets for each address. Returns how many packets went out.</summary>
    public static async Task<int> SendAsync(IEnumerable<string> macAddresses, string? lastKnownAddress = null)
    {
        List<byte[]> packets = macAddresses.Where(mac => ParseMac(mac) is not null).Select(BuildMagicPacket).ToList();
        if (packets.Count == 0)
        {
            return 0;
        }

        int sent = 0;
        foreach ((IPAddress local, IPAddress mask, IPAddress broadcast) in LocalInterfaces())
        {
            try
            {
                using UdpClient client = new(new IPEndPoint(local, 0)) { EnableBroadcast = true };
                foreach (byte[] packet in packets)
                {
                    foreach (int port in Ports)
                    {
                        sent += await TrySendAsync(client, packet, new IPEndPoint(IPAddress.Broadcast, port));
                        sent += await TrySendAsync(client, packet, new IPEndPoint(broadcast, port));
                        if (IPAddress.TryParse(lastKnownAddress, out IPAddress? last) && last.AddressFamily == AddressFamily.InterNetwork && SameSubnet(last, local, mask))
                        {
                            sent += await TrySendAsync(client, packet, new IPEndPoint(last, port));
                        }
                    }
                }
            }
            catch (SocketException)
            {
                // That adapter went away; the others still send.
            }
        }

        return sent;
    }

    private static async Task<int> TrySendAsync(UdpClient client, byte[] packet, IPEndPoint target)
    {
        try
        {
            await client.SendAsync(packet, packet.Length, target);
            return 1;
        }
        catch (SocketException)
        {
            return 0;
        }
    }

    /// <summary>Connected IPv4 addresses with their subnet mask and broadcast address.</summary>
    private static IEnumerable<(IPAddress Local, IPAddress Mask, IPAddress Broadcast)> LocalInterfaces()
    {
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null
                    || address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return (address.Address, address.IPv4Mask, DirectedBroadcast(address.Address, address.IPv4Mask));
            }
        }
    }

    internal static IPAddress DirectedBroadcast(IPAddress address, IPAddress mask)
    {
        byte[] bytes = address.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        for (int index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(bytes[index] | ~maskBytes[index]);
        }

        return new IPAddress(bytes);
    }

    internal static bool SameSubnet(IPAddress target, IPAddress local, IPAddress mask)
    {
        byte[] targetBytes = target.GetAddressBytes();
        byte[] localBytes = local.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        return targetBytes.Length == localBytes.Length
            && Enumerable.Range(0, maskBytes.Length).All(index => (targetBytes[index] & maskBytes[index]) == (localBytes[index] & maskBytes[index]));
    }
}