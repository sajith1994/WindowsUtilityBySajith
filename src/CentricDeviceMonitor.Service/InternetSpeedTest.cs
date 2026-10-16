using System.Diagnostics;
using System.Net;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.ServiceHost;

/// <summary>
/// Internet speed of this PC, measured against Cloudflare's public speed test endpoints (speed.cloudflare.com):
/// latency from small requests, download for up to 8 seconds, then an upload sized from the download speed so slow
/// links still finish in a few seconds.
/// </summary>
internal static class InternetSpeedTest
{
    private const string Server = "Cloudflare (speed.cloudflare.com)";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    public static async Task<RemoteInternetSpeedResult> RunAsync()
    {
        RemoteInternetSpeedResult result = new() { Server = Server };
        try
        {
            List<double> latencies = new();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                Stopwatch ping = Stopwatch.StartNew();
                using HttpResponseMessage response = await Http.GetAsync("https://speed.cloudflare.com/__down?bytes=0");
                response.EnsureSuccessStatusCode();
                latencies.Add(ping.Elapsed.TotalMilliseconds);
            }

            latencies.Sort();
            result.LatencyMs = latencies[latencies.Count / 2];

            result.DownloadMbps = await MeasureDownloadAsync(TimeSpan.FromSeconds(8));

            // About 4 seconds of upload at the download rate (uploads are usually slower), 1 to 25 MB.
            int uploadBytes = (int)Math.Clamp(result.DownloadMbps.Value * 125_000 * 4, 1_000_000, 25_000_000);
            byte[] payload = new byte[uploadBytes];
            Random.Shared.NextBytes(payload);
            Stopwatch upload = Stopwatch.StartNew();
            using (HttpResponseMessage response = await Http.PostAsync("https://speed.cloudflare.com/__up", new ByteArrayContent(payload)))
            {
                response.EnsureSuccessStatusCode();
            }

            result.UploadMbps = uploadBytes * 8 / upload.Elapsed.TotalSeconds / 1_000_000;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            result.Error = exception is TaskCanceledException
                ? "The internet speed test timed out on that PC (very slow or no internet)."
                : $"That PC could not reach the speed test server: {exception.Message}";
            ServiceLog.Write("Remote monitoring", $"Internet speed test failed: {exception.Message}");
        }

        return result;
    }

    /// <summary>
    /// Three connections, each fetching 25 MB pieces back to back until the time is up (the service refuses single
    /// downloads above about 50 MB). Parallel streams fill fast links that one connection cannot.
    /// </summary>
    private static async Task<double> MeasureDownloadAsync(TimeSpan duration)
    {
        long total = 0;
        Stopwatch watch = Stopwatch.StartNew();
        using CancellationTokenSource stop = new(duration);

        async Task WorkerAsync()
        {
            byte[] buffer = new byte[256 * 1024];
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using HttpResponseMessage response = await Http.GetAsync("https://speed.cloudflare.com/__down?bytes=25000000",
                        HttpCompletionOption.ResponseHeadersRead, stop.Token);
                    response.EnsureSuccessStatusCode();
                    await using Stream stream = await response.Content.ReadAsStreamAsync(stop.Token);
                    int read;
                    while ((read = await stream.ReadAsync(buffer, stop.Token)) > 0)
                    {
                        Interlocked.Add(ref total, read);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => WorkerAsync()));
        if (total == 0)
        {
            throw new HttpRequestException("No data arrived from the speed test server.");
        }

        return total * 8 / Math.Max(0.001, watch.Elapsed.TotalSeconds) / 1_000_000;
    }
}
