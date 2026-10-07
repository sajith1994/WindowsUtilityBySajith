using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor.Dialogs;

public partial class StorageDiagnosticsWindow : Window
{
    private readonly StorageDiagnosticsService _storageDiagnosticsService = new();
    private CancellationTokenSource? _benchmarkCancellation;
    private CancellationTokenSource? _capacityCancellation;
    private readonly DispatcherTimer _volumeRefreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private string _knownVolumeSignature = string.Empty;

    public StorageDiagnosticsWindow()
    {
        InitializeComponent();
        RefreshVolumes(force: true);

        _volumeRefreshTimer.Tick += VolumeRefreshTimer_Tick;
        _volumeRefreshTimer.Start();

        Loaded += async (_, _) => await RefreshHealthAsync();
        Closed += (_, _) =>
        {
            _volumeRefreshTimer.Stop();
            _benchmarkCancellation?.Cancel();
            _capacityCancellation?.Cancel();
        };
    }

    private async void VolumeRefreshTimer_Tick(object? sender, EventArgs e)
    {
        // Do not replace the selected volume object while a write/read operation is active.
        if (!VolumeComboBox.IsEnabled || !CapacityVolumeComboBox.IsEnabled)
        {
            return;
        }

        string previousSignature = _knownVolumeSignature;
        RefreshVolumes();
        if (!string.Equals(previousSignature, _knownVolumeSignature, StringComparison.Ordinal))
        {
            await RefreshHealthAsync();
        }
    }

    private void RefreshVolumesButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshVolumes(force: true);
    }

    private void RefreshVolumes(bool force = false)
    {
        string? selectedBenchmarkRoot = (VolumeComboBox.SelectedItem as StorageVolumeInfo)?.RootPath;
        string? selectedCapacityRoot = (CapacityVolumeComboBox.SelectedItem as StorageVolumeInfo)?.RootPath;

        IReadOnlyList<StorageVolumeInfo> volumes = _storageDiagnosticsService.GetTestableVolumes();
        string signature = string.Join(
            "|",
            volumes.Select(volume => $"{volume.RootPath.ToUpperInvariant()}:{volume.TotalBytes}:{volume.DriveType}:{volume.Label}"));

        if (!force && string.Equals(signature, _knownVolumeSignature, StringComparison.Ordinal))
        {
            return;
        }

        _knownVolumeSignature = signature;
        VolumeComboBox.ItemsSource = volumes;
        CapacityVolumeComboBox.ItemsSource = volumes;

        SelectVolumeByRoot(VolumeComboBox, selectedBenchmarkRoot);
        SelectVolumeByRoot(CapacityVolumeComboBox, selectedCapacityRoot);
        ConnectedVolumesText.Text = volumes.Count == 0
            ? "No local storage volumes available for testing."
            : $"{volumes.Count} local storage volume(s) available. USB/removable drives are detected automatically.";
        UpdateCapacitySafetyText();
    }

    private static void SelectVolumeByRoot(WpfComboBox comboBox, string? preferredRoot)
    {
        if (!string.IsNullOrWhiteSpace(preferredRoot))
        {
            foreach (object item in comboBox.Items)
            {
                if (item is StorageVolumeInfo volume &&
                    string.Equals(volume.RootPath, preferredRoot, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private async void RefreshHealthButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshVolumes(force: true);
        await RefreshHealthAsync();
    }

    private async Task RefreshHealthAsync()
    {
        HealthStatusText.Text = "Reading Windows storage counters...";
        try
        {
            IReadOnlyList<StorageDriveHealth> drives = await _storageDiagnosticsService.GetPhysicalDriveHealthAsync();
            HealthGrid.ItemsSource = drives;
            HealthStatusText.Text = drives.Count == 0
                ? "No physical disk health information was returned."
                : $"{drives.Count} physical disk(s) detected.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Storage health window", exception);
            HealthStatusText.Text = "Health information could not be read.";
            WpfMessageBox.Show(this, exception.Message, "Storage health", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RunBenchmarkButton_Click(object sender, RoutedEventArgs e)
    {
        if (VolumeComboBox.SelectedItem is not StorageVolumeInfo volume)
        {
            WpfMessageBox.Show(this, "Select a local storage volume first.", "Storage speed test", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (TestSizeComboBox.SelectedItem is not ComboBoxItem sizeItem || !int.TryParse(Convert.ToString(sizeItem.Tag), out int sizeMb))
        {
            sizeMb = 256;
        }

        int? requestedPasses = 1;
        if (BenchmarkPassCountComboBox.SelectedItem is ComboBoxItem passItem)
        {
            string tag = Convert.ToString(passItem.Tag) ?? "1";
            requestedPasses = string.Equals(tag, "CONTINUOUS", StringComparison.OrdinalIgnoreCase)
                ? null
                : int.TryParse(tag, out int parsedPasses)
                    ? Math.Clamp(parsedPasses, 1, 5)
                    : 1;
        }

        bool continuous = !requestedPasses.HasValue;
        int requestedPassCount = requestedPasses.GetValueOrDefault(1);
        string modeText = continuous
            ? "continuously until you press Stop / Cancel"
            : requestedPassCount == 1
                ? "for 1 pass"
                : $"for {requestedPassCount} passes";
        MessageBoxResult confirmation = WpfMessageBox.Show(
            this,
            $"Run a {sizeMb} MB sequential read/write test on {volume.RootPath} {modeText}?\n\nA temporary file is created and deleted after every pass. Multiple-pass and Continuous modes repeatedly write test data to the selected drive, so use an appropriate test size and stop Continuous mode when you have enough samples.",
            "Storage speed test",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _benchmarkCancellation?.Dispose();
        _benchmarkCancellation = new CancellationTokenSource();
        RunBenchmarkButton.IsEnabled = false;
        CancelBenchmarkButton.IsEnabled = true;
        VolumeComboBox.IsEnabled = false;
        TestSizeComboBox.IsEnabled = false;
        BenchmarkPassCountComboBox.IsEnabled = false;
        BenchmarkProgressBar.Value = 0;
        WriteSpeedText.Text = "-- MB/s";
        ReadSpeedText.Text = "-- MB/s";
        ContinuousSummaryText.Text = string.Empty;
        CacheWarningText.Visibility = Visibility.Collapsed;

        int completedPasses = 0;
        double totalWrite = 0;
        double totalRead = 0;
        double minWrite = double.MaxValue;
        double minRead = double.MaxValue;
        double maxWrite = 0;
        double maxRead = 0;

        try
        {
            while (!_benchmarkCancellation.IsCancellationRequested &&
                   (!requestedPasses.HasValue || completedPasses < requestedPassCount))
            {
                _benchmarkCancellation.Token.ThrowIfCancellationRequested();
                int currentPass = completedPasses + 1;
                string passLabel = requestedPasses.HasValue
                    ? $"Pass {currentPass} of {requestedPassCount}"
                    : $"Pass {currentPass} (Continuous)";

                BenchmarkProgressBar.Value = 0;
                BenchmarkStatusText.Text = $"{passLabel}: running sequential write test...";

                Progress<double> progress = new(value =>
                {
                    BenchmarkProgressBar.Value = value * 100.0;
                    BenchmarkStatusText.Text = value < 0.5
                        ? $"{passLabel}: running sequential write test..."
                        : $"{passLabel}: running sequential read test...";
                });

                StorageBenchmarkResult result = await _storageDiagnosticsService.RunSequentialBenchmarkAsync(
                    volume,
                    sizeMb,
                    progress,
                    _benchmarkCancellation.Token);

                completedPasses++;
                WriteSpeedText.Text = $"{result.SequentialWriteMegabytesPerSecond:0.0} MB/s";

                // Without cache bypass Windows serves the read back from RAM, which produces
                // figures far above what the drive can do. Show it as unmeasured rather than
                // printing a number a technician might act on.
                ReadSpeedText.Text = result.CacheBypassed
                    ? $"{result.SequentialReadMegabytesPerSecond:0.0} MB/s"
                    : $"{result.SequentialReadMegabytesPerSecond:0.0} MB/s (cached - not drive speed)";

                if (!result.CacheBypassed)
                {
                    CacheWarningText.Text =
                        "This volume rejected unbuffered I/O, so Windows served the read from RAM. " +
                        "The read figure reflects system memory, not the drive. See the app log for the reason.";
                    CacheWarningText.Visibility = Visibility.Visible;
                }

                totalWrite += result.SequentialWriteMegabytesPerSecond;
                totalRead += result.SequentialReadMegabytesPerSecond;
                minWrite = Math.Min(minWrite, result.SequentialWriteMegabytesPerSecond);
                minRead = Math.Min(minRead, result.SequentialReadMegabytesPerSecond);
                maxWrite = Math.Max(maxWrite, result.SequentialWriteMegabytesPerSecond);
                maxRead = Math.Max(maxRead, result.SequentialReadMegabytesPerSecond);

                string completionLabel = requestedPasses.HasValue
                    ? $"Pass {completedPasses} of {requestedPassCount}"
                    : $"{completedPasses} pass{(completedPasses == 1 ? string.Empty : "es")}";
                ContinuousSummaryText.Text =
                    $"{completionLabel} completed · Avg W {totalWrite / completedPasses:0.0} / R {totalRead / completedPasses:0.0} MB/s · " +
                    $"Peak W {maxWrite:0.0} / R {maxRead:0.0} · Min W {minWrite:0.0} / R {minRead:0.0}";

                BenchmarkProgressBar.Value = 100;
                bool morePasses = !requestedPasses.HasValue || completedPasses < requestedPassCount;
                BenchmarkStatusText.Text = morePasses
                    ? $"{completionLabel} complete. Starting the next pass..."
                    : $"Completed {completedPasses} pass{(completedPasses == 1 ? string.Empty : "es")}. Temporary benchmark file removed.";

                if (!morePasses)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), _benchmarkCancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            BenchmarkStatusText.Text = $"Stopped after {completedPasses} completed pass{(completedPasses == 1 ? string.Empty : "es")}. Temporary benchmark file removed.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Storage speed benchmark", exception);
            BenchmarkStatusText.Text = "Speed test failed.";
            WpfMessageBox.Show(this, exception.Message, "Storage speed test", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RunBenchmarkButton.IsEnabled = true;
            CancelBenchmarkButton.IsEnabled = false;
            VolumeComboBox.IsEnabled = true;
            TestSizeComboBox.IsEnabled = true;
            BenchmarkPassCountComboBox.IsEnabled = true;
            RefreshVolumes(force: true);
        }
    }

    private void CancelBenchmarkButton_Click(object sender, RoutedEventArgs e)
    {
        _benchmarkCancellation?.Cancel();
    }

    private void CapacityVolumeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateCapacitySafetyText();
    }

    private void UpdateCapacitySafetyText()
    {
        if (CapacityVolumeComboBox.SelectedItem is not StorageVolumeInfo volume)
        {
            CapacitySafetyText.Text = "Select a volume to see the maximum safe test size.";
            return;
        }

        try
        {
            long safe = _storageDiagnosticsService.GetSafeCapacityVerificationBytes(volume);
            string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty;
            bool systemVolume = string.Equals(Path.GetPathRoot(volume.RootPath), systemRoot, StringComparison.OrdinalIgnoreCase);
            CapacitySafetyText.Text = safe <= 0
                ? "Not enough safely usable free space for verification."
                : $"Maximum safe test region: {StorageCapacityVerificationResult.FormatBytes(safe)}. " +
                  (systemVolume ? "A 20 GB reserve is kept on the Windows system volume." : "A free-space safety reserve is kept automatically.");
        }
        catch
        {
            CapacitySafetyText.Text = "Could not calculate safe free space for this volume.";
        }
    }

    private async void RunCapacityButton_Click(object sender, RoutedEventArgs e)
    {
        if (CapacityVolumeComboBox.SelectedItem is not StorageVolumeInfo volume)
        {
            WpfMessageBox.Show(this, "Select a local storage volume first.", "Storage capacity verification", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        long safeMaximum = _storageDiagnosticsService.GetSafeCapacityVerificationBytes(volume);
        if (safeMaximum <= 0)
        {
            WpfMessageBox.Show(this, "There is not enough safely usable free space on the selected volume.", "Storage capacity verification", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long requestedBytes = safeMaximum;
        bool allFreeSpace = true;
        if (CapacityAmountComboBox.SelectedItem is ComboBoxItem amountItem)
        {
            string tag = Convert.ToString(amountItem.Tag) ?? "ALL";
            if (!string.Equals(tag, "ALL", StringComparison.OrdinalIgnoreCase) && long.TryParse(tag, out long gb))
            {
                requestedBytes = gb * 1024L * 1024L * 1024L;
                allFreeSpace = false;
            }
        }

        if (requestedBytes > safeMaximum)
        {
            WpfMessageBox.Show(
                this,
                $"The selected amount is larger than the safe free-space region.\n\nMaximum safe amount: {StorageCapacityVerificationResult.FormatBytes(safeMaximum)}",
                "Storage capacity verification",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty;
        bool systemVolume = string.Equals(Path.GetPathRoot(volume.RootPath), systemRoot, StringComparison.OrdinalIgnoreCase);
        if (systemVolume && allFreeSpace)
        {
            WpfMessageBox.Show(
                this,
                "Full free-space verification is disabled on the Windows system volume. A counterfeit drive can wrap writes onto earlier physical storage and corrupt Windows or existing files. Use a secondary/empty drive for a full real-capacity test, or choose a limited test amount for C:\\.",
                "Full capacity test blocked on system drive",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string warning =
            $"Verify {StorageCapacityVerificationResult.FormatBytes(requestedBytes)} of FREE SPACE on {volume.RootPath}?\n\n" +
            "The test writes unique data, then reads every block back. At the file-system level it only uses free space, but on counterfeit/overstated flash a physical wraparound can corrupt existing data. Use an empty or non-critical drive for a full test. The selected free space is temporarily consumed and repeated full-drive testing adds write wear to SSDs.\n\n" +
            (allFreeSpace
                ? "You selected ALL SAFE FREE SPACE. This is the strongest test for detecting fake/overstated capacity."
                : "A partial test only proves the region that is actually tested; it cannot prove the drive's entire advertised capacity.");

        if (WpfMessageBox.Show(this, warning, "Storage capacity verification", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (allFreeSpace && WpfMessageBox.Show(
                this,
                "Confirm full free-space verification. Keep the computer powered and do not remove the drive until the verification finishes or you cancel it.",
                "Confirm full capacity test",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _capacityCancellation?.Dispose();
        _capacityCancellation = new CancellationTokenSource();
        RunCapacityButton.IsEnabled = false;
        CancelCapacityButton.IsEnabled = true;
        CapacityVolumeComboBox.IsEnabled = false;
        CapacityAmountComboBox.IsEnabled = false;
        CapacityProgressBar.Value = 0;
        CapacityResultText.Text = string.Empty;
        CapacityStatusText.Text = "Preparing two-step verification...";

        WriteStepStatusText.Text = "Preparing";
        WriteStepProgressBar.Value = 0;
        WriteCurrentSpeedText.Text = "-- MB/s";
        WriteAverageSpeedText.Text = "-- MB/s";
        WriteFastestSpeedText.Text = "-- MB/s";
        WriteElapsedText.Text = "00:00:00";
        WriteEtaText.Text = "Calculating...";
        WriteProgressText.Text = $"0 B / {StorageCapacityVerificationResult.FormatBytes(requestedBytes)}";

        VerifyStepStatusText.Text = "Waiting for step 1";
        VerifyStepProgressBar.Value = 0;
        VerifyCurrentSpeedText.Text = "-- MB/s";
        VerifyAverageSpeedText.Text = "-- MB/s";
        VerifyFastestSpeedText.Text = "-- MB/s";
        VerifyElapsedText.Text = "00:00:00";
        VerifyEtaText.Text = "Waiting";
        VerifyProgressText.Text = $"0 B / {StorageCapacityVerificationResult.FormatBytes(requestedBytes)}";

        CapacityOverallPercentText.Text = "Overall progress 0.0%";
        CapacityTotalElapsedText.Text = "Total elapsed 00:00:00";

        Progress<StorageCapacityProgress> progress = new(value =>
        {
            bool writing = value.IsWritePhase;
            bool verifying = value.IsVerifyPhase;

            CapacityProgressBar.Value = value.Fraction * 100.0;
            WriteStepProgressBar.Value = value.WriteFraction * 100.0;
            VerifyStepProgressBar.Value = value.VerifyFraction * 100.0;

            WriteStepStatusText.Text = value.WriteFraction >= 0.9999
                ? "Complete"
                : writing ? "Writing" : "Waiting";
            WriteCurrentSpeedText.Text = value.WriteCurrentMegabytesPerSecond > 0.01
                ? $"{value.WriteCurrentMegabytesPerSecond:0.0} MB/s"
                : writing ? "Measuring..." : "-- MB/s";
            WriteAverageSpeedText.Text = FormatSpeed(value.WriteAverageMegabytesPerSecond, writing);
            WriteFastestSpeedText.Text = FormatSpeed(value.WriteFastestMegabytesPerSecond, writing);
            WriteElapsedText.Text = FormatDuration(value.WriteElapsed);
            WriteEtaText.Text = value.WriteFraction >= 0.9999
                ? "00:00:00"
                : value.WriteEstimatedRemaining is TimeSpan writeEta ? FormatDuration(writeEta) : "Calculating...";
            WriteProgressText.Text = $"{StorageCapacityVerificationResult.FormatBytes(value.WrittenBytes)} / {StorageCapacityVerificationResult.FormatBytes(value.RequestedBytes)}";

            VerifyStepStatusText.Text = value.VerifyFraction >= 0.9999
                ? "Complete"
                : verifying ? "Verifying" : "Waiting for step 1";
            VerifyCurrentSpeedText.Text = value.VerifyCurrentMegabytesPerSecond > 0.01
                ? $"{value.VerifyCurrentMegabytesPerSecond:0.0} MB/s"
                : verifying ? "Measuring..." : "-- MB/s";
            VerifyAverageSpeedText.Text = FormatSpeed(value.VerifyAverageMegabytesPerSecond, verifying);
            VerifyFastestSpeedText.Text = FormatSpeed(value.VerifyFastestMegabytesPerSecond, verifying);
            VerifyElapsedText.Text = FormatDuration(value.VerifyElapsed);
            VerifyEtaText.Text = value.VerifyFraction >= 0.9999
                ? "00:00:00"
                : verifying && value.VerifyEstimatedRemaining is TimeSpan verifyEta ? FormatDuration(verifyEta) : "Waiting";
            VerifyProgressText.Text = $"{StorageCapacityVerificationResult.FormatBytes(value.VerifiedBytes)} / {StorageCapacityVerificationResult.FormatBytes(value.RequestedBytes)}";

            CapacityOverallPercentText.Text = $"Overall progress {value.Fraction * 100.0:0.0}%";
            CapacityTotalElapsedText.Text = $"Total elapsed {FormatDuration(value.TotalElapsed)}";

            long phaseBytes = verifying ? value.VerifiedBytes : value.WrittenBytes;
            string stepName = verifying ? "Step 2 - reading/verifying" : "Step 1 - writing/filling";
            CapacityStatusText.Text = $"{stepName}: {StorageCapacityVerificationResult.FormatBytes(phaseBytes)} / {StorageCapacityVerificationResult.FormatBytes(value.RequestedBytes)}";
        });

        try
        {
            // Run the full write/hash/read workload on a worker thread so the WPF dispatcher
            // remains free for window movement, resize, repaint and button input.
            StorageCapacityVerificationResult result = await Task.Run(
                () => _storageDiagnosticsService.RunCapacityVerificationAsync(
                    volume,
                    requestedBytes,
                    progress,
                    _capacityCancellation.Token),
                _capacityCancellation.Token);

            CapacityProgressBar.Value = 100;
            if (result.Passed)
            {
                CapacityStatusText.Text = "Verification completed. Step 1 write and Step 2 read-back both finished successfully.";
                WriteStepStatusText.Text = "Complete";
                VerifyStepStatusText.Text = "Complete";
                WriteStepProgressBar.Value = 100;
                VerifyStepProgressBar.Value = 100;
                WriteEtaText.Text = "00:00:00";
                VerifyEtaText.Text = "00:00:00";
                CapacityOverallPercentText.Text = "Overall progress 100.0%";
                CapacityResultText.Text = $"PASSED: {result.TestedText} was written and read back without corruption.";
            }
            else
            {
                CapacityStatusText.Text = "Verification failed. Temporary test files were removed.";
                VerifyStepStatusText.Text = "Failed";
                VerifyEtaText.Text = "--:--:--";
                CapacityResultText.Text = $"FAILED: {result.FailureReason}";
                WpfMessageBox.Show(
                    this,
                    $"Storage verification failed.\n\n{result.FailureReason}\n\nThis can indicate counterfeit/overstated capacity, failing flash memory, cabling/controller problems, or file-system corruption.",
                    "Storage capacity verification",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (OperationCanceledException)
        {
            CapacityStatusText.Text = "Verification cancelled. Temporary test files are being removed.";
            if (VerifyStepProgressBar.Value > 0)
            {
                VerifyStepStatusText.Text = "Cancelled";
            }
            else
            {
                WriteStepStatusText.Text = "Cancelled";
            }
            CapacityResultText.Text = "Cancelled - no final capacity result was produced.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Storage capacity verification", exception);
            CapacityStatusText.Text = "Capacity verification failed.";
            if (VerifyStepProgressBar.Value > 0)
            {
                VerifyStepStatusText.Text = "Failed";
            }
            else
            {
                WriteStepStatusText.Text = "Failed";
            }
            CapacityResultText.Text = exception.Message;
            WpfMessageBox.Show(this, exception.Message, "Storage capacity verification", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RunCapacityButton.IsEnabled = true;
            CancelCapacityButton.IsEnabled = false;
            CapacityVolumeComboBox.IsEnabled = true;
            CapacityAmountComboBox.IsEnabled = true;
            RefreshVolumes(force: true);
        }
    }

    private void CancelCapacityButton_Click(object sender, RoutedEventArgs e)
    {
        _capacityCancellation?.Cancel();
    }

    private void OpenBenchmarkLogButton_Click(object sender, RoutedEventArgs e)
    {
        OpenCsvLog(_storageDiagnosticsService.GetBenchmarkLogPath(), "RecordedAt,Volume,TestSizeMB,SequentialWriteMBps,SequentialReadMBps,Profile,CacheBypassed\r\n", "Storage benchmark log");
    }

    private void OpenCapacityLogButton_Click(object sender, RoutedEventArgs e)
    {
        OpenCsvLog(_storageDiagnosticsService.GetCapacityVerificationLogPath(), "RecordedAt,Volume,RequestedBytes,WrittenBytes,VerifiedBytes,Passed,FailedBlockIndex,FailureReason\r\n", "Storage capacity verification log");
    }

    private void OpenCsvLog(string path, string header, string title)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, header);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException($"Open {title}", exception);
            WpfMessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string FormatSpeed(double megabytesPerSecond, bool activePhase)
    {
        if (megabytesPerSecond > 0.01)
        {
            return $"{megabytesPerSecond:0.0} MB/s";
        }

        return activePhase ? "Measuring..." : "-- MB/s";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
