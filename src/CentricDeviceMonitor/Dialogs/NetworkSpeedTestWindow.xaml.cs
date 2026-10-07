using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor.Dialogs;

public partial class NetworkSpeedTestWindow : Window
{
    private readonly NetworkSpeedTestService _speedTestService = new();
    private readonly NetworkQualityTestService _networkQualityTestService = new();
    private readonly LanThroughputService _lanThroughputService = new();
    private CancellationTokenSource? _packetLossCancellation;
    private CancellationTokenSource? _lanTestCancellation;
    private bool _packetLossTestRunning;
    private bool _lanTestRunning;
    private bool _closing;
    private string _activeGatewayAddress = string.Empty;
    private double _lanMeterMaximumMbps = 1000;

    public NetworkSpeedTestWindow()
    {
        InitializeComponent();
        _lanThroughputService.ServerActivity += LanThroughputService_ServerActivity;
        Loaded += async (_, _) => await RefreshLinkAsync();
    }

    private async Task RefreshLinkAsync()
    {
        try
        {
            StatusText.Text = "Reading active network link...";
            NetworkLinkSnapshot link = await _speedTestService.GetActiveLinkAsync(
                checkInternet: true,
                measureGatewayLatency: true);

            AdapterNameText.Text = link.AdapterName == "Not available"
                ? link.AdapterName
                : $"{link.AdapterName} • {link.AdapterDescription}";
            ConnectionTypeText.Text = link.InterfaceType;
            LocalAddressText.Text = $"{link.LocalIpAddress} • gateway {link.GatewayAddress}";
            string previousGatewayAddress = _activeGatewayAddress;
            _activeGatewayAddress = string.Equals(link.GatewayAddress, "Not available", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : link.GatewayAddress;
            UseGatewayButton.IsEnabled = !string.IsNullOrWhiteSpace(_activeGatewayAddress);
            if (!string.IsNullOrWhiteSpace(_activeGatewayAddress) &&
                (string.IsNullOrWhiteSpace(PacketLossTargetText.Text) ||
                 string.Equals(PacketLossTargetText.Text.Trim(), previousGatewayAddress, StringComparison.OrdinalIgnoreCase)))
            {
                PacketLossTargetText.Text = _activeGatewayAddress;
                PacketLossStatusText.Text = $"Ready to test the detected gateway {_activeGatewayAddress}.";
            }
            NegotiatedLinkText.Text = link.NegotiatedLinkMbps.HasValue
                ? NetworkLinkSnapshot.FormatMbps(link.NegotiatedLinkMbps.Value)
                : "Not reported";

            if (link.InterfaceType.Equals("Wi-Fi", StringComparison.OrdinalIgnoreCase))
            {
                string rx = link.WifiReceiveMbps.HasValue ? $"Rx {link.WifiReceiveMbps.Value:0.#} Mbps" : "Rx not reported";
                string tx = link.WifiTransmitMbps.HasValue ? $"Tx {link.WifiTransmitMbps.Value:0.#} Mbps" : "Tx not reported";
                WifiRatesText.Text = $"{rx} • {tx}";
                string signal = link.WifiSignalPercent.HasValue ? $"{link.WifiSignalPercent.Value}% signal" : "signal not reported";
                WifiDetailsText.Text = $"{signal} • {link.WifiRadioType} • channel {link.WifiChannel}";
            }
            else
            {
                WifiRatesText.Text = "Not applicable to the active Ethernet link";
                WifiDetailsText.Text = "Not applicable";
            }

            GatewayLatencyText.Text = link.GatewayLatencyMs.HasValue
                ? $"{link.GatewayLatencyMs.Value:0.#} ms average"
                : link.GatewayAddress == "Not available" ? "No IPv4 gateway reported" : "Gateway did not reply to ping";
            string currentRx = link.CurrentReceiveMbps.HasValue ? $"Rx {link.CurrentReceiveMbps.Value:0.##} Mbps" : "Rx not reported";
            string currentTx = link.CurrentTransmitMbps.HasValue ? $"Tx {link.CurrentTransmitMbps.Value:0.##} Mbps" : "Tx not reported";
            CurrentTrafficText.Text = $"{currentRx} • {currentTx}";
            InternetAvailabilityText.Text = link.InternetStatus;
            LanServerAddressText.Text = link.LocalIpAddress;
            double suggestedMeterMaximum = link.NegotiatedLinkMbps ??
                Math.Max(link.WifiReceiveMbps ?? 0, link.WifiTransmitMbps ?? 0);
            SetLanMeterMaximum(ChooseLanMeterMaximum(suggestedMeterMaximum > 0 ? suggestedMeterMaximum : 1000));
            StatusText.Text = link.InternetAvailable
                ? "Local link refreshed. FAST.com is reachable and LAN testing is ready."
                : "Local link refreshed. FAST.com was not reachable, but the LAN throughput test works without internet.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Network speed test - refresh link", exception);
            StatusText.Text = "Unable to read the active link. LAN testing can still be attempted by entering the other PC's IP address manually.";
        }
    }

    private async void RefreshLinkButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLinkAsync();
    }

    private void UseGatewayButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_activeGatewayAddress))
        {
            PacketLossStatusText.Text = "No default IPv4 gateway was detected. Enter a local IP address or hostname manually.";
            return;
        }

        PacketLossTargetText.Text = _activeGatewayAddress;
        PacketLossStatusText.Text = $"Ready to test the detected gateway {_activeGatewayAddress}.";
    }

    private async void RunPacketLossButton_Click(object sender, RoutedEventArgs e)
    {
        if (_packetLossTestRunning)
        {
            return;
        }

        if (_lanTestRunning)
        {
            WpfMessageBox.Show(this, "Wait for the LAN throughput test to finish before running packet-loss samples, so the tests do not affect each other's results.", "Packet loss & link quality", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string target = PacketLossTargetText.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            WpfMessageBox.Show(
                this,
                "Enter the default gateway, another local PC, an IP address or a hostname to test.",
                "Packet loss & link quality",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            PacketLossTargetText.Focus();
            return;
        }

        int packetCount = ReadComboTag(PacketCountComboBox, 25);
        int intervalMilliseconds = ReadComboTag(PacketIntervalComboBox, 250);

        // Tag 0 on the packet-count list means "run until stopped".
        bool continuous = packetCount <= 0;

        _packetLossTestRunning = true;
        _packetLossCancellation = new CancellationTokenSource();
        SetPacketLossControlsEnabled(false);
        RunLanTestButton.IsEnabled = false;
        ResetPacketLossResults(continuous ? 0 : packetCount);
        PacketLossStatusText.Text = continuous
            ? $"Testing {target} continuously every {intervalMilliseconds} ms. Press Stop to finish."
            : $"Testing {target} with {packetCount} packets...";
        StatusText.Text = $"Running packet-loss test against {target}...";

        // A continuous run has no known end, so show an indeterminate bar instead of a fake one.
        PacketLossProgressBar.IsIndeterminate = continuous;

        Progress<NetworkQualityTestProgress> progress = new(item =>
        {
            if (!continuous)
            {
                PacketLossProgressBar.Maximum = item.TotalPackets;
                PacketLossProgressBar.Value = item.PacketsSent;
            }
            PacketLossPercentText.Text = $"{item.PacketLossPercent:0.#}%";
            PacketCountsText.Text = $"{item.PacketsReceived} / {item.PacketsSent}";
            PacketAverageLatencyText.Text = item.AverageLatencyMs.HasValue
                ? $"{item.AverageLatencyMs.Value:0.#} ms"
                : "-- ms";
            PacketJitterText.Text = item.JitterMs.HasValue
                ? $"{item.JitterMs.Value:0.#} ms"
                : "-- ms";
            PacketLossStatusText.Text = item.LastStatus;
            SetPacketLossBrush(item.PacketLossPercent);
        });

        try
        {
            NetworkQualityTestResult result = await _networkQualityTestService.RunAsync(
                target,
                packetCount,
                intervalMilliseconds,
                progress,
                _packetLossCancellation.Token,
                continuous);

            DisplayPacketLossResult(result);
            StatusText.Text = continuous
                ? $"Continuous test stopped after {result.PacketsSent} packets against {result.Target}: {result.PacketLossPercent:0.#}% loss."
                : $"Packet-loss test completed against {result.Target}: {result.PacketLossPercent:0.#}% loss, {result.Quality.ToLowerInvariant()} quality.";
        }
        catch (OperationCanceledException)
        {
            PacketQualityText.Text = "Cancelled";
            PacketQualityText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            PacketLossStatusText.Text = "The packet-loss test was stopped.";
            PacketInterpretationText.Text = "Partial packet counts are shown above; run a complete test for a quality grade.";
            StatusText.Text = "Packet-loss test cancelled.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Packet loss and link quality test", exception);
            PacketQualityText.Text = "Unable to test";
            PacketQualityText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            PacketLossStatusText.Text = exception.Message;
            PacketInterpretationText.Text = "Confirm the target address and network connection. A firewall may block ICMP ping replies.";
            StatusText.Text = "Packet-loss test failed.";
            WpfMessageBox.Show(
                this,
                exception.Message,
                "Packet loss & link quality",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _packetLossTestRunning = false;
            PacketLossProgressBar.IsIndeterminate = false;
            SetPacketLossControlsEnabled(true);
            RunLanTestButton.IsEnabled = true;
            _packetLossCancellation?.Dispose();
            _packetLossCancellation = null;
        }
    }

    private void CancelPacketLossButton_Click(object sender, RoutedEventArgs e)
    {
        _packetLossCancellation?.Cancel();
    }

    private void SetPacketLossControlsEnabled(bool enabled)
    {
        PacketLossTargetText.IsEnabled = enabled;
        PacketCountComboBox.IsEnabled = enabled;
        PacketIntervalComboBox.IsEnabled = enabled;
        UseGatewayButton.IsEnabled = enabled && !string.IsNullOrWhiteSpace(_activeGatewayAddress);
        RunPacketLossButton.IsEnabled = enabled;
        CancelPacketLossButton.IsEnabled = !enabled;
    }

    private void ResetPacketLossResults(int packetCount)
    {
        // packetCount is 0 for a continuous run, where there is no target to count towards.
        PacketLossProgressBar.Maximum = packetCount > 0 ? packetCount : 1;
        PacketLossProgressBar.Value = 0;
        PacketQualityText.Text = "Testing...";
        PacketQualityText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
        PacketLossPercentText.Text = "0%";
        PacketCountsText.Text = packetCount > 0 ? $"0 / {packetCount}" : "0 / --";
        PacketAverageLatencyText.Text = "-- ms";
        PacketJitterText.Text = "-- ms";
        PacketLatencyRangeText.Text = "Latency range: --";
        PacketInterpretationText.Text = "Collecting replies. Zero packet loss is expected on a healthy local wired path.";
        SetPacketLossBrush(0);
    }

    private void DisplayPacketLossResult(NetworkQualityTestResult result)
    {
        PacketLossProgressBar.Maximum = result.PacketsSent;
        PacketLossProgressBar.Value = result.PacketsSent;
        PacketQualityText.Text = result.Quality;
        PacketQualityText.SetResourceReference(
            TextBlock.ForegroundProperty,
            result.Quality switch
            {
                "Excellent" or "Good" => "SuccessBrush",
                "Fair" => "WarningBrush",
                _ => "DangerBrush"
            });
        PacketLossPercentText.Text = $"{result.PacketLossPercent:0.#}%";
        PacketCountsText.Text = $"{result.PacketsReceived} / {result.PacketsSent}";
        PacketAverageLatencyText.Text = FormatMilliseconds(result.AverageLatencyMs);
        PacketJitterText.Text = FormatMilliseconds(result.JitterMs);
        PacketLatencyRangeText.Text = result.MinimumLatencyMs.HasValue && result.MaximumLatencyMs.HasValue
            ? $"Latency range: {result.MinimumLatencyMs.Value:0.#}-{result.MaximumLatencyMs.Value:0.#} ms"
            : "Latency range: no successful replies";
        PacketInterpretationText.Text = result.Interpretation;
        PacketLossStatusText.Text = $"Completed {result.CompletedAt:HH:mm:ss} against {result.Target}: {result.PacketsLost} of {result.PacketsSent} packet(s) lost.";
        SetPacketLossBrush(result.PacketLossPercent);
    }

    private void SetPacketLossBrush(double lossPercent)
    {
        PacketLossPercentText.SetResourceReference(
            TextBlock.ForegroundProperty,
            lossPercent <= 0 ? "SuccessBrush" : lossPercent <= 3 ? "WarningBrush" : "DangerBrush");
    }

    private static string FormatMilliseconds(double? value)
    {
        return value.HasValue ? $"{value.Value:0.#} ms" : "-- ms";
    }

    private void OpenFastComButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://fast.com/")
            {
                UseShellExecute = true
            });
            StatusText.Text = "Opened FAST.com in the default browser. Use Show more info there for upload and latency.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Open FAST.com", exception);
            WpfMessageBox.Show(this, exception.Message, "Open FAST.com", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void StartLanServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lanThroughputService.IsServerRunning)
        {
            return;
        }

        if (!TryParsePort(LanServerPortText.Text, out int port))
        {
            ShowPortError();
            return;
        }

        try
        {
            await _lanThroughputService.StartServerAsync(port);
            LanServerPortText.IsEnabled = false;
            StartLanServerButton.IsEnabled = false;
            StopLanServerButton.IsEnabled = true;
            LanServerStatusText.Text = $"Running on TCP {port}. Keep this window open while the other PC tests.";
            StatusText.Text = $"LAN test server started on TCP {port}.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Start LAN throughput server", exception);
            WpfMessageBox.Show(
                this,
                $"Unable to start the LAN test server.\n\n{exception.Message}\n\nIf the port is blocked, use the firewall button or choose another port.",
                "LAN throughput server",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void StopLanServerButton_Click(object sender, RoutedEventArgs e)
    {
        await StopLanServerUiAsync();
    }

    private async Task StopLanServerUiAsync()
    {
        await _lanThroughputService.StopServerAsync();
        if (!_closing)
        {
            LanServerPortText.IsEnabled = true;
            StartLanServerButton.IsEnabled = true;
            StopLanServerButton.IsEnabled = false;
            LanServerStatusText.Text = "Stopped";
            StatusText.Text = "LAN test server stopped.";
        }
    }

    private async void AllowLanFirewallButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParsePort(LanServerPortText.Text, out int port))
        {
            ShowPortError();
            return;
        }

        MessageBoxResult confirmation = WpfMessageBox.Show(
            this,
            $"Allow inbound TCP port {port} through Windows Firewall for Private and Domain network profiles?\n\nOnly do this on a trusted local network. The rule is used by the temporary LAN speed-test server.",
            "Allow LAN speed-test port",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        string ruleName = $"Windows Utility LAN Speed Test TCP {port}";
        try
        {
            await RunNetshAsync($"advfirewall firewall delete rule name=\"{ruleName}\"");
            (int exitCode, string output, string error) = await RunNetshAsync(
                $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port} profile=private,domain");

            if (exitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
            }

            StatusText.Text = $"Windows Firewall now allows TCP {port} on Private/Domain profiles.";
            WpfMessageBox.Show(
                this,
                $"Firewall rule added for TCP {port} on Private and Domain profiles.",
                "LAN speed-test firewall rule",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Allow LAN throughput firewall rule", exception);
            WpfMessageBox.Show(this, exception.Message, "LAN speed-test firewall rule", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RunLanTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lanTestRunning)
        {
            return;
        }

        if (_packetLossTestRunning)
        {
            WpfMessageBox.Show(this, "Wait for the packet-loss test to finish before running LAN throughput, so the ping traffic does not affect the speed result.", "LAN speed test", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string host = LanTargetText.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            WpfMessageBox.Show(this, "Enter the IP address or hostname of the other PC running the LAN test server.", "LAN speed test", MessageBoxButton.OK, MessageBoxImage.Information);
            LanTargetText.Focus();
            return;
        }

        if (!TryParsePort(LanClientPortText.Text, out int port))
        {
            ShowPortError();
            return;
        }

        int duration = ReadComboTag(LanDurationComboBox, 10);
        int streams = ReadComboTag(LanStreamsComboBox, 4);

        _lanTestRunning = true;
        _lanTestCancellation = new CancellationTokenSource();
        RunLanTestButton.IsEnabled = false;
        RunPacketLossButton.IsEnabled = false;
        CancelLanTestButton.IsEnabled = true;
        LanTargetText.IsEnabled = false;
        LanClientPortText.IsEnabled = false;
        LanDurationComboBox.IsEnabled = false;
        LanStreamsComboBox.IsEnabled = false;
        LanTestProgressBar.IsIndeterminate = true;
        LanTestProgressBar.Minimum = 0;
        LanTestProgressBar.Maximum = 100;
        LanTestProgressBar.Value = 0;
        LanLatencyText.Text = "-- ms";
        LanUploadText.Text = "-- Mbps";
        LanDownloadText.Text = "-- Mbps";
        ResetLanMeters();

        Progress<LanSpeedTestProgress> progress = new(item =>
        {
            LanTestPhaseText.Text = item.Phase;
            LanTestDetailText.Text = item.Detail;
            StatusText.Text = item.Detail;
            if (item.LatencyMs.HasValue)
            {
                LanLatencyText.Text = $"{item.LatencyMs.Value:0.#} ms";
            }
            if (item.UploadMbps.HasValue)
            {
                LanUploadText.Text = $"{item.UploadMbps.Value:0.#} Mbps";
            }
            if (item.DownloadMbps.HasValue)
            {
                LanDownloadText.Text = $"{item.DownloadMbps.Value:0.#} Mbps";
            }

            if (item.ProgressPercent.HasValue)
            {
                LanTestProgressBar.IsIndeterminate = false;
                LanTestProgressBar.Value = Math.Clamp(item.ProgressPercent.Value, 0, 100);
            }

            UpdateLanMeters(item.UploadMbps, item.DownloadMbps);
            if (item.CurrentMbps.HasValue)
            {
                string direction = string.Equals(item.Phase, "Upload", StringComparison.OrdinalIgnoreCase)
                    ? "upload"
                    : "download";
                LanMeterLiveText.Text = $"Live {direction}: {NetworkLinkSnapshot.FormatMbps(item.CurrentMbps.Value)} • running average shown by the meter.";
            }
        });

        try
        {
            LanThroughputResult result = await _lanThroughputService.RunClientTestAsync(
                host,
                port,
                duration,
                streams,
                progress,
                _lanTestCancellation.Token);

            LanLatencyText.Text = result.LatencyMs.HasValue ? $"{result.LatencyMs.Value:0.#} ms" : "Not available";
            LanUploadText.Text = $"{result.UploadMbps:0.#} Mbps";
            LanDownloadText.Text = $"{result.DownloadMbps:0.#} Mbps";
            UpdateLanMeters(result.UploadMbps, result.DownloadMbps);
            LanTestProgressBar.IsIndeterminate = false;
            LanTestProgressBar.Value = 100;
            LanMeterLiveText.Text = $"Completed averages • Upload {NetworkLinkSnapshot.FormatMbps(result.UploadMbps)} • Download {NetworkLinkSnapshot.FormatMbps(result.DownloadMbps)}";
            LanTestPhaseText.Text = "Complete";
            LanTestDetailText.Text = $"Completed {result.CompletedAt:HH:mm:ss} against {result.ServerHost}:{result.Port} using {result.ParallelStreams} TCP stream(s). Upload {result.UploadMbps:0.#} Mbps • Download {result.DownloadMbps:0.#} Mbps.";
            StatusText.Text = "Actual LAN throughput test completed.";
        }
        catch (OperationCanceledException)
        {
            LanTestPhaseText.Text = "Cancelled";
            LanTestDetailText.Text = "The LAN throughput test was stopped.";
            LanMeterLiveText.Text = "Test cancelled. The meters retain the last partial averages.";
            StatusText.Text = "LAN throughput test cancelled.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("LAN throughput test", exception);
            LanTestPhaseText.Text = "Unable to complete";
            LanTestDetailText.Text = $"{exception.Message} Make sure the server is running on the other PC and Windows Firewall allows the selected TCP port.";
            LanMeterLiveText.Text = "The LAN test could not complete. Any partial meter values are not a final result.";
            StatusText.Text = "LAN throughput test failed.";
            WpfMessageBox.Show(
                this,
                $"{exception.Message}\n\nChecklist:\n• Start the LAN test server on the other PC.\n• Confirm its IP address and TCP port.\n• Allow the port through Windows Firewall on the server PC.\n• Make sure both PCs are on the same LAN/VLAN or routing permits the connection.",
                "LAN speed test",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _lanTestRunning = false;
            LanTestProgressBar.IsIndeterminate = false;
            RunLanTestButton.IsEnabled = true;
            RunPacketLossButton.IsEnabled = true;
            CancelLanTestButton.IsEnabled = false;
            LanTargetText.IsEnabled = true;
            LanClientPortText.IsEnabled = true;
            LanDurationComboBox.IsEnabled = true;
            LanStreamsComboBox.IsEnabled = true;
            _lanTestCancellation?.Dispose();
            _lanTestCancellation = null;
        }
    }

    private void CancelLanTestButton_Click(object sender, RoutedEventArgs e)
    {
        _lanTestCancellation?.Cancel();
    }

    private void ResetLanMeters()
    {
        LanUploadMeter.Value = 0;
        LanDownloadMeter.Value = 0;
        LanUploadMeterText.Text = "0 Mbps";
        LanDownloadMeterText.Text = "0 Mbps";
        LanMeterLiveText.Text = "Measuring generated TCP traffic; the bars show the running average for each direction.";
    }

    private void UpdateLanMeters(double? uploadMbps, double? downloadMbps)
    {
        double observedMaximum = Math.Max(uploadMbps ?? 0, downloadMbps ?? 0);
        if (observedMaximum > _lanMeterMaximumMbps)
        {
            SetLanMeterMaximum(ChooseLanMeterMaximum(observedMaximum * 1.08));
        }

        if (uploadMbps.HasValue)
        {
            LanUploadMeter.Value = Math.Clamp(uploadMbps.Value, 0, _lanMeterMaximumMbps);
            LanUploadMeterText.Text = NetworkLinkSnapshot.FormatMbps(uploadMbps.Value);
        }

        if (downloadMbps.HasValue)
        {
            LanDownloadMeter.Value = Math.Clamp(downloadMbps.Value, 0, _lanMeterMaximumMbps);
            LanDownloadMeterText.Text = NetworkLinkSnapshot.FormatMbps(downloadMbps.Value);
        }
    }

    private void SetLanMeterMaximum(double maximumMbps)
    {
        _lanMeterMaximumMbps = Math.Max(10, maximumMbps);
        LanUploadMeter.Maximum = _lanMeterMaximumMbps;
        LanDownloadMeter.Maximum = _lanMeterMaximumMbps;
        string formattedMaximum = NetworkLinkSnapshot.FormatMbps(_lanMeterMaximumMbps);
        LanMeterScaleText.Text = $"Scale: 0-{formattedMaximum}";
        LanMeterMaximumText.Text = formattedMaximum;
    }

    private static double ChooseLanMeterMaximum(double requiredMbps)
    {
        double[] commonScales =
        {
            10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 25000, 40000, 100000
        };

        foreach (double scale in commonScales)
        {
            if (requiredMbps <= scale)
            {
                return scale;
            }
        }

        return Math.Ceiling(requiredMbps / 100000.0) * 100000.0;
    }

    private void LanThroughputService_ServerActivity(string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closing)
            {
                LanServerStatusText.Text = message;
            }
        }));
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _closing = true;
        _packetLossCancellation?.Cancel();
        _lanTestCancellation?.Cancel();
        _lanThroughputService.ServerActivity -= LanThroughputService_ServerActivity;
        await _lanThroughputService.DisposeAsync();
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lanTestRunning || _packetLossTestRunning)
        {
            MessageBoxResult result = WpfMessageBox.Show(
                this,
                "A network test is still running. Stop it and close this window?",
                "Network Speed & Bandwidth",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            _packetLossCancellation?.Cancel();
            _lanTestCancellation?.Cancel();
        }

        if (_lanThroughputService.IsServerRunning)
        {
            await StopLanServerUiAsync();
        }

        _closing = true;
        Close();
    }

    private static bool TryParsePort(string value, out int port)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1024 and <= 65535;
    }

    private void ShowPortError()
    {
        WpfMessageBox.Show(this, "Enter a TCP port between 1024 and 65535. Port 5201 is the recommended default.", "LAN speed test", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static int ReadComboTag(WpfComboBox comboBox, int fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item &&
               int.TryParse(item.Tag?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunNetshAsync(string arguments)
    {
        ProcessStartInfo startInfo = new("netsh.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("netsh.exe could not be started.");
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output.Trim(), error.Trim());
    }
}
