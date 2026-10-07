using System.Diagnostics;
using System.Net.NetworkInformation;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed class NetworkQualityTestService
{
    private const int PingTimeoutMilliseconds = 1000;
    private static readonly byte[] PingPayload = CreatePingPayload();

    /// <param name="packetCount">
    /// Number of packets to send. Ignored when <paramref name="continuous"/> is true.
    /// </param>
    /// <param name="continuous">
    /// When true the test runs until cancelled. Cancelling a continuous run is the normal way to
    /// finish it, so the partial results are returned rather than thrown away.
    /// </param>
    public async Task<NetworkQualityTestResult> RunAsync(
        string target,
        int packetCount,
        int intervalMilliseconds,
        IProgress<NetworkQualityTestProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool continuous = false)
    {
        string normalizedTarget = target.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            throw new ArgumentException("Enter a gateway, local IP address or hostname to test.", nameof(target));
        }

        if (!continuous && packetCount is < 5 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(packetCount), "Packet count must be between 5 and 200.");
        }

        // The floor is 5 ms so fast LAN links can be sampled finely; below that the ping call
        // overhead dominates and the interval stops being meaningful.
        if (intervalMilliseconds is < 5 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds), "Packet interval must be between 5 and 5000 milliseconds.");
        }

        List<double> successfulLatencies = new();
        int packetsReceived = 0;
        int packetsSent = 0;

        for (int packetNumber = 1; continuous || packetNumber <= packetCount; packetNumber++)
        {
            // A cancelled continuous run is a normal finish, not an error.
            if (cancellationToken.IsCancellationRequested)
            {
                if (continuous)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            packetsSent = packetNumber;
            string countLabel = continuous ? packetNumber.ToString() : $"{packetNumber}/{packetCount}";

            double? latencyMs = null;
            string lastStatus;
            try
            {
                using Ping ping = new();
                Stopwatch roundTrip = Stopwatch.StartNew();
                PingReply reply = await ping.SendPingAsync(
                        normalizedTarget,
                        PingTimeoutMilliseconds,
                        PingPayload)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                roundTrip.Stop();

                if (reply.Status == IPStatus.Success)
                {
                    // PingReply.RoundtripTime is whole milliseconds, so a fast LAN or a local
                    // gateway reports a flat 0 ms and jitter collapses to nothing. Fall back to
                    // the measured elapsed time to keep sub-millisecond detail.
                    double measured = reply.RoundtripTime > 0
                        ? reply.RoundtripTime
                        : roundTrip.Elapsed.TotalMilliseconds;

                    latencyMs = measured;
                    successfulLatencies.Add(measured);
                    packetsReceived++;
                    lastStatus = $"Reply {countLabel}: {measured:0.##} ms";
                }
                else
                {
                    lastStatus = $"No reply {countLabel}: {FormatIpStatus(reply.Status)}";
                }
            }
            catch (OperationCanceledException) when (continuous)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastStatus = $"No reply {countLabel}: {GetFriendlyPingError(exception)}";
            }

            progress?.Report(new NetworkQualityTestProgress
            {
                PacketsSent = packetNumber,
                PacketsReceived = packetsReceived,
                TotalPackets = continuous ? packetNumber : packetCount,
                LastLatencyMs = latencyMs,
                AverageLatencyMs = successfulLatencies.Count == 0 ? null : successfulLatencies.Average(),
                JitterMs = CalculateJitter(successfulLatencies),
                LastStatus = lastStatus
            });

            if (continuous || packetNumber < packetCount)
            {
                try
                {
                    await Task.Delay(intervalMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (continuous)
                {
                    break;
                }
            }
        }

        double lossPercent = packetsSent == 0 ? 0 : (packetsSent - packetsReceived) * 100.0 / packetsSent;
        double? minimumLatency = successfulLatencies.Count == 0 ? null : successfulLatencies.Min();
        double? averageLatency = successfulLatencies.Count == 0 ? null : successfulLatencies.Average();
        double? maximumLatency = successfulLatencies.Count == 0 ? null : successfulLatencies.Max();
        double? jitter = CalculateJitter(successfulLatencies);
        string quality = ClassifyQuality(lossPercent, averageLatency, jitter);

        return new NetworkQualityTestResult
        {
            Target = normalizedTarget,
            PacketsSent = packetsSent,
            PacketsReceived = packetsReceived,
            MinimumLatencyMs = minimumLatency,
            AverageLatencyMs = averageLatency,
            MaximumLatencyMs = maximumLatency,
            JitterMs = jitter,
            Quality = quality,
            Interpretation = BuildInterpretation(quality, lossPercent),
            CompletedAt = DateTime.Now
        };
    }

    private static double? CalculateJitter(IReadOnlyList<double> samples)
    {
        if (samples.Count < 2)
        {
            return null;
        }

        double totalVariation = 0;
        for (int index = 1; index < samples.Count; index++)
        {
            totalVariation += Math.Abs(samples[index] - samples[index - 1]);
        }

        return totalVariation / (samples.Count - 1);
    }

    private static string ClassifyQuality(double lossPercent, double? averageLatency, double? jitter)
    {
        if (!averageLatency.HasValue)
        {
            return "No response";
        }

        double jitterValue = jitter ?? 0;
        if (lossPercent <= 0 && averageLatency.Value <= 25 && jitterValue <= 5)
        {
            return "Excellent";
        }

        if (lossPercent <= 1 && averageLatency.Value <= 75 && jitterValue <= 15)
        {
            return "Good";
        }

        if (lossPercent <= 3 && averageLatency.Value <= 150 && jitterValue <= 30)
        {
            return "Fair";
        }

        return "Poor";
    }

    private static string BuildInterpretation(string quality, double lossPercent)
    {
        if (string.Equals(quality, "No response", StringComparison.Ordinal))
        {
            return "The target did not answer. Confirm the address and connection; ICMP may also be blocked by a firewall.";
        }

        if (lossPercent <= 0)
        {
            return quality is "Excellent" or "Good"
                ? "No packet loss was detected and the path is stable. On a wired LAN this is the expected result."
                : "No packets were lost, but latency variation is elevated. Check congestion, Wi-Fi interference or device load.";
        }

        return "Packet loss was detected. When testing the local gateway or another wired PC, retest after checking the cable, connectors, NIC and switch port; on Wi-Fi also check signal and interference.";
    }

    private static string FormatIpStatus(IPStatus status)
    {
        return status switch
        {
            IPStatus.TimedOut => "timed out",
            IPStatus.DestinationHostUnreachable => "host unreachable",
            IPStatus.DestinationNetworkUnreachable => "network unreachable",
            IPStatus.BadDestination => "invalid destination",
            IPStatus.PacketTooBig => "packet too large",
            _ => status.ToString()
        };
    }

    private static string GetFriendlyPingError(Exception exception)
    {
        Exception actual = exception is PingException { InnerException: { } innerException }
            ? innerException
            : exception;
        return string.IsNullOrWhiteSpace(actual.Message) ? "ping failed" : actual.Message;
    }

    private static byte[] CreatePingPayload()
    {
        byte[] payload = new byte[32];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)('A' + index % 26);
        }

        return payload;
    }
}
