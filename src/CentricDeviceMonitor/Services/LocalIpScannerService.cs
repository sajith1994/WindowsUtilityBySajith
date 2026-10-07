using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed record LocalSubnetInfo(
    string AdapterName,
    string LocalAddress,
    string Cidr,
    IReadOnlyList<string> Addresses);

public sealed record IpScanProgress(int Completed, int Total, IpScanResult? Result, string Stage = "Scanning");

public sealed class LocalIpScannerService
{
    private static readonly Regex ArpLineRegex = new(
        @"^\s*(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s+(?<mac>[0-9A-Fa-f]{2}(?:-[0-9A-Fa-f]{2}){5})\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly NetworkDeviceService _networkDeviceService = new();

    public LocalSubnetInfo? GetLocalSubnet()
    {
        IEnumerable<NetworkInterface> candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface =>
                networkInterface.OperationalStatus == OperationalStatus.Up &&
                (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                 networkInterface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
            .OrderByDescending(HasIpv4Gateway);

        foreach (NetworkInterface networkInterface in candidates)
        {
            try
            {
                UnicastIPAddressInformation? unicast = networkInterface
                    .GetIPProperties()
                    .UnicastAddresses
                    .FirstOrDefault(address =>
                        address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address.Address));

                if (unicast?.IPv4Mask is null)
                {
                    continue;
                }

                int prefixLength = CountPrefixBits(unicast.IPv4Mask);
                if (prefixLength <= 0 || prefixLength >= 32)
                {
                    continue;
                }

                // Keep the scanner intentionally local and small. If the LAN is broader than /24,
                // scan only the /24 containing this PC. Narrower subnets such as /25 are respected.
                int scanPrefixLength = Math.Max(prefixLength, 24);
                uint ip = ToUInt32(unicast.Address);
                uint mask = PrefixToMask(scanPrefixLength);
                uint network = ip & mask;
                uint broadcast = network | ~mask;

                List<string> addresses = new();
                for (uint value = network + 1; value < broadcast && addresses.Count < 254; value++)
                {
                    if (value == ip)
                    {
                        continue;
                    }

                    addresses.Add(FromUInt32(value).ToString());
                }

                return new LocalSubnetInfo(
                    networkInterface.Name,
                    unicast.Address.ToString(),
                    $"{FromUInt32(network)}/{scanPrefixLength}",
                    addresses);
            }
            catch
            {
                // Try the next active local adapter.
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<IpScanResult>> ScanAsync(
        LocalSubnetInfo subnet,
        IProgress<IpScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int total = subnet.Addresses.Count;
        int completed = 0;
        ConcurrentDictionary<string, long?> respondingAddresses = new(StringComparer.OrdinalIgnoreCase);

        // Phase 1: perform only the ICMP sweep. Keep all work off the WPF UI context and
        // limit concurrency so lower-powered 720p systems remain responsive during scans.
        using SemaphoreSlim pingLimiter = new(48, 48);
        IEnumerable<Task> pingTasks = subnet.Addresses.Select(async address =>
        {
            await pingLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using Ping ping = new();
                PingReply reply = await ping.SendPingAsync(address, 400)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (reply.Status == IPStatus.Success)
                {
                    respondingAddresses[address] = reply.RoundtripTime;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // An unreachable/protected address is expected during a subnet scan.
            }
            finally
            {
                int current = Interlocked.Increment(ref completed);
                // Do not flood the Dispatcher with 250+ progress callbacks. Results are reported
                // separately after discovery/enrichment; the progress bar updates every 4 hosts.
                if (current == total || current % 4 == 0)
                {
                    progress?.Report(new IpScanProgress(current, total, null, "Pinging"));
                }

                pingLimiter.Release();
            }
        });

        await Task.WhenAll(pingTasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // A local ping attempt causes Windows to resolve ARP even when a device blocks ICMP.
        // Read the ARP cache once instead of calling SendARP synchronously for every address.
        IReadOnlyDictionary<string, string> arpEntries = await ReadArpCacheAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> candidateAddresses = new(respondingAddresses.Keys, StringComparer.OrdinalIgnoreCase);
        HashSet<string> allowedAddresses = new(subnet.Addresses, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> entry in arpEntries)
        {
            if (allowedAddresses.Contains(entry.Key))
            {
                candidateAddresses.Add(entry.Key);
            }
        }

        List<IpScanResult> discovered = new();
        object resultLock = new();
        using SemaphoreSlim enrichmentLimiter = new(16, 16);

        IEnumerable<Task> enrichmentTasks = candidateAddresses.Select(async address =>
        {
            await enrichmentLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string hostname = await _networkDeviceService.ResolveHostnameAsync(address, cancellationToken)
                    .ConfigureAwait(false);

                string macAddress = arpEntries.TryGetValue(address, out string? cachedMac)
                    ? cachedMac
                    : _networkDeviceService.ResolveLocalMacAddress(address);

                IpScanResult result = new()
                {
                    IpAddress = address,
                    Hostname = hostname,
                    MacAddress = macAddress,
                    RoundTripTime = respondingAddresses.TryGetValue(address, out long? roundTrip)
                        ? roundTrip
                        : null
                };

                lock (resultLock)
                {
                    discovered.Add(result);
                }

                progress?.Report(new IpScanProgress(total, total, result, "Resolving devices"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // One hostname/MAC failure should not stop the rest of the scan.
            }
            finally
            {
                enrichmentLimiter.Release();
            }
        });

        await Task.WhenAll(enrichmentTasks).ConfigureAwait(false);

        return discovered
            .OrderBy(result => ParseIpv4(result.IpAddress))
            .ToList();
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadArpCacheAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, string> entries = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "arp.exe",
                    Arguments = "-a",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                throw;
            }

            string output = await outputTask.ConfigureAwait(false);

            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match match = ArpLineRegex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                string address = match.Groups["ip"].Value;
                string macAddress = match.Groups["mac"].Value.ToUpperInvariant();
                if (IPAddress.TryParse(address, out IPAddress? parsed) &&
                    parsed.AddressFamily == AddressFamily.InterNetwork)
                {
                    entries[address] = macAddress;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // ARP enrichment is optional. Ping responders will still be returned.
        }

        return entries;
    }

    private static bool HasIpv4Gateway(NetworkInterface networkInterface)
    {
        try
        {
            return networkInterface.GetIPProperties().GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any));
        }
        catch
        {
            return false;
        }
    }

    private static int CountPrefixBits(IPAddress mask)
    {
        int count = 0;
        foreach (byte value in mask.GetAddressBytes())
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                if ((value & (1 << bit)) != 0)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static uint PrefixToMask(int prefixLength) =>
        prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);

    private static uint ToUInt32(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    private static IPAddress FromUInt32(uint value) => new(new byte[]
    {
        (byte)(value >> 24),
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value
    });

    private static uint ParseIpv4(string address) =>
        IPAddress.TryParse(address, out IPAddress? parsed) ? ToUInt32(parsed) : uint.MaxValue;
}
