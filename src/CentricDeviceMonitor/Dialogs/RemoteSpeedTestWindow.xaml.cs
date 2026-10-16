using System.IO;
using System.Net.Http;
using System.Windows;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.Dialogs;

/// <summary>
/// Speed tests for a monitored PC: the local network between this PC and that PC (response time, then 6 seconds
/// each way), and that PC's own internet connection (measured on that PC and reported back).
/// </summary>
public partial class RemoteSpeedTestWindow : Window
{
    private static readonly TimeSpan LanDuration = TimeSpan.FromSeconds(6);
    private readonly RemoteMonitorClient _client;
    private readonly RemotePcEntry _pc;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _running;

    public RemoteSpeedTestWindow(RemoteMonitorClient client, RemotePcEntry pc, string displayName)
    {
        InitializeComponent();
        _client = client;
        _pc = pc;
        HeadingText.Text = $"Speed test: {displayName}";
        SubheadingText.Text = $"{pc.Host}  •  Results depend on both PCs' connections (Wi-Fi or cable) and on other traffic.";
        LanDescriptionText.Text = $"Between this PC and {displayName}: response time, then 6 seconds of data each way.";
        LanDownloadLabel.Text = $"Download (from {displayName})";
        LanUploadLabel.Text = $"Upload (to {displayName})";
        InternetTitleText.Text = $"Internet on {displayName}";
        InternetDescriptionText.Text = $"{displayName} tests its own internet connection against Cloudflare's speed test and reports back. Takes about 15 seconds and uses roughly 50-150 MB of its data.";
        Closed += (_, _) => _lifetime.Cancel();
    }

    private async void LanTestButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async token =>
        {
            LanLatencyText.Text = LanDownloadText.Text = LanUploadText.Text = "--";
            SetStatus("Measuring response time...");
            double latency = await _client.MeasureLatencyAsync(_pc.Host, _pc.Port, token);
            LanLatencyText.Text = latency < 1 ? "<1 ms" : $"{latency:0} ms";

            SetStatus("Measuring download (6 seconds)...");
            LanDownloadText.Text = FormatMbps(await _client.MeasureLanDownloadAsync(_pc, LanDuration, token));

            SetStatus("Measuring upload (6 seconds)...");
            LanUploadText.Text = FormatMbps(await _client.MeasureLanUploadAsync(_pc, LanDuration, token));

            SetStatus($"Local network test finished at {DateTime.Now:HH:mm:ss}.");
            ApplicationLogService.WriteMessage("Network PCs speed test",
                $"{_pc.Host}: LAN {LanLatencyText.Text}, download {LanDownloadText.Text}, upload {LanUploadText.Text}.");
        });
    }

    private async void InternetTestButton_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async token =>
        {
            InternetLatencyText.Text = InternetDownloadText.Text = InternetUploadText.Text = "--";
            SetStatus("The other PC is testing its internet connection (about 15 seconds)...");
            RemoteInternetSpeedResult result = await _client.RunInternetSpeedTestAsync(_pc, token);
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                SetStatus(result.Error, error: true);
                return;
            }

            InternetLatencyText.Text = result.LatencyMs is double latency ? $"{latency:0} ms" : "--";
            InternetDownloadText.Text = result.DownloadMbps is double down ? FormatMbps(down) : "--";
            InternetUploadText.Text = result.UploadMbps is double up ? FormatMbps(up) : "--";
            SetStatus($"Internet test finished at {DateTime.Now:HH:mm:ss}  •  {result.Server}");
            ApplicationLogService.WriteMessage("Network PCs speed test",
                $"{_pc.Host}: internet latency {InternetLatencyText.Text}, download {InternetDownloadText.Text}, upload {InternetUploadText.Text}.");
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> test)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        LanTestButton.IsEnabled = InternetTestButton.IsEnabled = false;
        TestProgress.Visibility = Visibility.Visible;
        try
        {
            await test(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Window closed during the test.
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException)
        {
            SetStatus(exception switch
            {
                InvalidOperationException => exception.Message,
                TaskCanceledException => "The test timed out. The other PC may be busy, asleep or on a slow connection.",
                _ => $"The test stopped: {exception.Message}"
            }, error: true);
        }
        finally
        {
            _running = false;
            if (!_lifetime.IsCancellationRequested)
            {
                LanTestButton.IsEnabled = InternetTestButton.IsEnabled = true;
                TestProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void SetStatus(string text, bool error = false)
    {
        StatusLine.Text = text;
        StatusLine.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, error ? "DangerBrush" : "MutedTextBrush");
    }

    private static string FormatMbps(double mbps) => mbps >= 1000 ? $"{mbps / 1000:0.00} Gbps" : mbps >= 100 ? $"{mbps:0} Mbps" : $"{mbps:0.0} Mbps";

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
