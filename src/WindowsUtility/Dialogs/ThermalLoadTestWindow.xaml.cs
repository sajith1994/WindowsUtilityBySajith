using System.ComponentModel;
using System.Globalization;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsUtility.Controls;
using WindowsUtility.Services;
using WpfApplication = System.Windows.Application;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfMessageBox = System.Windows.MessageBox;

namespace WindowsUtility.Dialogs;

public partial class ThermalLoadTestWindow : Window
{
    private readonly HardwareMonitorService _hardwareMonitorService = new();
    private readonly GraphicsThermalProfile _graphicsThermalProfile = HardwareMonitorService.GetGraphicsThermalProfile();
    private readonly CpuLoadGenerator _cpuLoadGenerator = new();
    private readonly GpuRenderLoadController _gpuRenderLoadController;
    private readonly GpuComputeLoadController _gpuComputeLoadController = new();
    private bool _usingComputeLoad;
    private readonly DispatcherTimer _monitorTimer;
    private bool _readingSensors;
    private bool _testRunning;
    private bool _closing;
    private DateTime _testStartedAt;
    private TimeSpan _testDuration;
    private ThermalTestMode _activeMode;
    private double _cpuStopTemperature;
    private double _gpuStopTemperature;
    private double? _peakCpuTemperature;
    private double? _peakGpuTemperature;

    // Live graph and result tracking.
    private const int MaxChartSamples = 3600;
    private readonly SystemResourceMonitorService _resourceMonitor = new();
    private readonly List<DateTime> _chartTimes = new();
    private readonly List<(DateTime Time, string Label)> _chartMarkers = new();
    private readonly LiveChartSeries _cpuTempSeries = new("CPU temperature", "°C", System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
    private readonly LiveChartSeries _gpuTempSeries = new("GPU temperature", "°C", System.Windows.Media.Color.FromRgb(0xE1, 0x1D, 0x48));
    private readonly LiveChartSeries _cpuLoadSeries = new("CPU load", "%", System.Windows.Media.Color.FromRgb(0x25, 0x63, 0xEB), 0, 100);
    private readonly LiveChartSeries _cpuPowerSeries = new("CPU package power", "W", System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06));
    private readonly LiveChartSeries _cpuClockSeries = new("CPU clock", "GHz", System.Windows.Media.Color.FromRgb(0x7C, 0x3A, 0xED));
    private readonly LiveChartSeries _gpuLoadSeries = new("GPU load", "%", System.Windows.Media.Color.FromRgb(0x08, 0x91, 0xB2), 0, 100);
    private readonly LiveChartSeries _fanSeries = new("CPU fan", "RPM", System.Windows.Media.Color.FromRgb(0x16, 0x93, 0x4A));
    private LiveChartSeries[] AllSeries => new[] { _cpuTempSeries, _gpuTempSeries, _cpuLoadSeries, _cpuPowerSeries, _cpuClockSeries, _gpuLoadSeries, _fanSeries };
    private double? _startCpuTemperature;
    private double? _startGpuTemperature;
    private double? _startFanRpm;
    private double? _maxFanRpm;
    private double _powerTotal;
    private int _powerSamples;
    private double? _powerMax;
    private int _activeLoadPercent;
    private DateTime? _testEndedAt;

    public ThermalLoadTestWindow()
    {
        InitializeComponent();
        _gpuRenderLoadController = new GpuRenderLoadController(GpuLoadViewport);
        PopulateGpuAdapters();
        // Temperatures, load, power and fan matter most for a cooler check; clock and GPU load are one click away.
        _cpuClockSeries.IsVisible = false;
        _gpuLoadSeries.IsVisible = false;
        foreach (LiveChartSeries series in AllSeries)
        {
            ChartSeriesChips.Children.Add(LiveGraphPanel.CreateSeriesChip(series, RedrawChart));
        }

        ThermalChart.SetData(_chartTimes, AllSeries, TimeSpan.FromMinutes(2));
        UpdateChartThresholds();
        SetState("Ready", "MutedTextBrush");
        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _monitorTimer.Tick += async (_, _) => await MonitorTimerTickAsync();

        Loaded += async (_, _) =>
        {
            RenderingTierText.Text = GpuRenderLoadController.IsHardwareRenderingAvailable
                ? _graphicsThermalProfile.IsIntegratedOnly
                    ? $"Hardware tier {GpuRenderLoadController.RenderingTier} · shared CPU thermal sensor"
                    : $"Hardware rendering tier {GpuRenderLoadController.RenderingTier}"
                : "Hardware rendering unavailable";
            RenderingTierText.ToolTip = _graphicsThermalProfile.Diagnostic;
            UpdateConfigurationAvailability();
            _monitorTimer.Start();
            await RefreshSensorReadingsAsync(enforceSafety: false);
        };
    }

    private async void StartTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _testRunning || AcknowledgeRiskCheckBox.IsChecked != true)
        {
            return;
        }

        ThermalTestMode mode = GetSelectedMode();
        int durationSeconds = GetSelectedInteger(DurationChoices, 60);
        int loadPercent = GetSelectedInteger(LoadChoices, 100);
        double cpuLimit = CpuLimitSlider.Value;
        double gpuLimit = GpuLimitSlider.Value;

        StartTestButton.IsEnabled = false;
        StatusText.Text = "Checking required temperature sensors...";

        ThermalSensorSnapshot snapshot;
        try
        {
            snapshot = await ReadSensorSnapshotAsync();
            if (_closing)
            {
                return;
            }

            DisplaySensorSnapshot(snapshot);
        }
        catch (Exception exception)
        {
            if (_closing)
            {
                return;
            }

            ApplicationLogService.WriteException("Thermal load test preflight", exception);
            StatusText.Text = "Sensor preflight failed.";
            SetRunningState(false);
            WpfMessageBox.Show(this, exception.Message, "Thermal load test", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string? validationError = ValidateStart(mode, snapshot, cpuLimit, gpuLimit);
        if (validationError is not null)
        {
            StatusText.Text = "Test not started.";
            SetRunningState(false);
            WpfMessageBox.Show(this, validationError, "Thermal load test", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        GpuSafetyReading gpuSafety = ResolveGpuSafetyReading(snapshot);
        string gpuSafetyText = UsesGpu(mode)
            ? gpuSafety.UsesSharedCpuSensor
                ? "\nGPU safety sensor: shared CPU package temperature (integrated graphics)."
                : $"\nGPU safety sensor: {gpuSafety.SensorName}."
            : string.Empty;
        MessageBoxResult confirmation = WpfMessageBox.Show(
            this,
            $"Start a {durationSeconds}-second {GetModeDisplay(mode)} load at {loadPercent}%?\n\n" +
            $"Automatic stop limits: CPU {cpuLimit:0} °C, GPU {gpuLimit:0} °C. " +
            $"Keep this computer supervised during the test.{gpuSafetyText}",
            "Start thermal load test",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmation != MessageBoxResult.Yes)
        {
            StatusText.Text = "Ready. Test was not started.";
            SetRunningState(false);
            return;
        }

        if (_closing)
        {
            return;
        }

        try
        {
            _activeMode = mode;
            _testDuration = TimeSpan.FromSeconds(durationSeconds);
            _cpuStopTemperature = cpuLimit;
            _gpuStopTemperature = gpuLimit;
            _peakCpuTemperature = IsValidTemperature(snapshot.Cpu.ValueCelsius)
                ? snapshot.Cpu.ValueCelsius.GetValueOrDefault()
                : null;
            _peakGpuTemperature = IsValidTemperature(gpuSafety.ValueCelsius)
                ? gpuSafety.ValueCelsius.GetValueOrDefault()
                : null;

            if (UsesCpu(mode))
            {
                _cpuLoadGenerator.Start(loadPercent);
            }

            if (UsesGpu(mode))
            {
                // The WPF render load is vsync-locked and cannot exceed roughly a third of a
                // modern discrete card. Use the compute load when the adapter accepts a D3D11
                // device, and keep the render load only as a fallback.
                _usingComputeLoad = _gpuComputeLoadController.TryInitialize(SelectedAdapterDescription());
                if (_usingComputeLoad)
                {
                    _gpuComputeLoadController.Start(loadPercent);
                }
                else
                {
                    GpuLoadModeText.Text =
                        $"Direct3D compute unavailable ({_gpuComputeLoadController.LastError}). " +
                        "Falling back to the vsync-limited WPF render load, which will not reach full GPU power.";
                    _gpuRenderLoadController.Start(loadPercent);
                }
            }

            _testStartedAt = DateTime.Now;
            _testRunning = true;
            BeginResultTracking(snapshot, gpuSafety, loadPercent);
            SetRunningState(true);
            TestProgressBar.Value = 0;
            StatusText.Text = "Thermal load is running. Keep the system supervised.";
            SetState("Running", "PrimaryBrush");
            ActiveLoadText.Text = $"{loadPercent}%";
            WorkerDetailText.Text = BuildWorkerDetail(mode);
            GpuPreviewPlaceholder.Visibility = UsesGpu(mode) ? Visibility.Collapsed : Visibility.Visible;
            await RefreshSensorReadingsAsync(enforceSafety: true);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Start thermal load test", exception);
            _gpuComputeLoadController.Stop();
            _gpuRenderLoadController.Stop();
            await _cpuLoadGenerator.StopAsync();
            if (_closing)
            {
                return;
            }

            _testRunning = false;
            SetRunningState(false);
            StatusText.Text = "The thermal load could not be started.";
            WpfMessageBox.Show(this, exception.Message, "Thermal load test", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopTestButton_Click(object sender, RoutedEventArgs e)
    {
        await StopTestAsync("Stopped by the user.", playAlert: false);
    }

    private async Task MonitorTimerTickAsync()
    {
        if (_closing)
        {
            return;
        }

        UpdateTimeDisplay();
        if (_testRunning && DateTime.Now - _testStartedAt >= _testDuration)
        {
            await StopTestAsync("Completed the selected test duration.", playAlert: false);
            return;
        }

        await RefreshSensorReadingsAsync(enforceSafety: _testRunning);
    }

    private async Task RefreshSensorReadingsAsync(bool enforceSafety)
    {
        if (_readingSensors)
        {
            return;
        }

        _readingSensors = true;
        try
        {
            ThermalSensorSnapshot snapshot = await ReadSensorSnapshotAsync();
            if (_closing)
            {
                return;
            }

            DisplaySensorSnapshot(snapshot);

            if (!_testRunning || !enforceSafety)
            {
                return;
            }

            if (UsesCpu(_activeMode))
            {
                if (!snapshot.Cpu.IsAvailable ||
                    !snapshot.Cpu.IsCpuSpecific ||
                    !IsValidTemperature(snapshot.Cpu.ValueCelsius))
                {
                    await StopTestAsync("Safety stop: CPU temperature telemetry became unavailable.", playAlert: true);
                    return;
                }

                double cpuTemperature = snapshot.Cpu.ValueCelsius.GetValueOrDefault();
                if (cpuTemperature >= _cpuStopTemperature)
                {
                    await StopTestAsync(
                        $"Safety stop: CPU reached {cpuTemperature:0.0} °C (limit {_cpuStopTemperature:0} °C).",
                        playAlert: true);
                    return;
                }
            }

            if (UsesGpu(_activeMode))
            {
                GpuSafetyReading gpuSafety = ResolveGpuSafetyReading(snapshot);
                if (!gpuSafety.IsAvailable || !IsValidTemperature(gpuSafety.ValueCelsius))
                {
                    await StopTestAsync("Safety stop: GPU temperature telemetry became unavailable.", playAlert: true);
                    return;
                }

                double gpuTemperature = gpuSafety.ValueCelsius.GetValueOrDefault();
                if (gpuTemperature >= _gpuStopTemperature)
                {
                    await StopTestAsync(
                        gpuSafety.UsesSharedCpuSensor
                            ? $"Safety stop: shared CPU package sensor reached {gpuTemperature:0.0} °C during integrated-GPU load (GPU limit {_gpuStopTemperature:0} °C)."
                            : $"Safety stop: GPU reached {gpuTemperature:0.0} °C (limit {_gpuStopTemperature:0} °C).",
                        playAlert: true);
                }
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Thermal load sensor refresh", exception);
            SensorDetailText.Text = $"Sensor read failed: {exception.Message}";
            if (_testRunning && enforceSafety)
            {
                await StopTestAsync("Safety stop: temperature telemetry could not be read.", playAlert: true);
            }
        }
        finally
        {
            _readingSensors = false;
        }
    }

    private Task<ThermalSensorSnapshot> ReadSensorSnapshotAsync()
    {
        return Task.Run(() => new ThermalSensorSnapshot(
            _hardwareMonitorService.GetCpuTemperatureReading(),
            _hardwareMonitorService.GetGpuTemperatureReading(),
            _hardwareMonitorService.GetCpuClockReading(),
            _hardwareMonitorService.GetCpuPackagePowerReading(),
            _hardwareMonitorService.GetFanInventoryReading(),
            _hardwareMonitorService.GetGpuPowerReading(),
            _hardwareMonitorService.GetGpuUtilisationReading(),
            _hardwareMonitorService.GetGpuMemoryReading(),
            ReadCpuUsage()));
    }

    private double? ReadCpuUsage()
    {
        try
        {
            return _resourceMonitor.Read().CpuUsagePercent;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fan RPM matters more here than anywhere else in the app: the point of a thermal load test
    /// is to see whether the coolers ramp up and hold the temperature.
    /// </summary>
    private void ApplyFans(FanInventoryReading inventory)
    {
        if (!inventory.IsAvailable)
        {
            CpuFanText.Text = "CPU fan: not detected";
            CaseFanText.Text = "Case fans: not detected";
            GpuFanText.Text = "GPU fans: not detected";
            return;
        }

        List<FanSensorReading> cpuFans = inventory.Fans.Where(fan => fan.Group == FanGroup.Cpu).ToList();
        List<FanSensorReading> caseFans = inventory.Fans.Where(fan => fan.Group == FanGroup.System).ToList();
        List<FanSensorReading> gpuFans = inventory.GpuFans.ToList();

        CpuFanText.Text = cpuFans.Count > 0
            ? "CPU fan: " + string.Join(", ", cpuFans.Select(fan => $"{fan.Rpm:0} RPM"))
            : "CPU fan: not detected";
        CaseFanText.Text = caseFans.Count > 0
            ? "Case fans: " + string.Join(", ", caseFans.Select(fan => $"{fan.Rpm:0} RPM"))
            : "Case fans: none reporting";
        GpuFanText.Text = gpuFans.Count > 0
            ? "GPU fans: " + string.Join(", ", gpuFans.Select(fan => $"{fan.Rpm:0} RPM"))
            : "GPU fans: not exposed by the driver";
    }

    private void DisplaySensorSnapshot(ThermalSensorSnapshot snapshot)
    {
        CpuTemperatureText.Text = snapshot.Cpu.IsAvailable && IsValidTemperature(snapshot.Cpu.ValueCelsius)
            ? $"{snapshot.Cpu.ValueCelsius.GetValueOrDefault():0.0} °C"
            : "Not available";
        GpuSafetyReading gpuSafety = ResolveGpuSafetyReading(snapshot);
        GpuTemperatureText.Text = gpuSafety.IsAvailable && IsValidTemperature(gpuSafety.ValueCelsius)
            ? $"{gpuSafety.ValueCelsius.GetValueOrDefault():0.0} °C"
            : "Not available";
        CpuClockText.Text = snapshot.CpuClock.Display;
        CpuClockSourceText.Text = snapshot.CpuClock.IsAvailable ? snapshot.CpuClock.Source : "Clock unavailable";
        CpuClockText.ToolTip = snapshot.CpuClock.Diagnostic;
        CpuClockSourceText.ToolTip = snapshot.CpuClock.Diagnostic;
        CpuPowerText.Text = snapshot.CpuPower.Display;
        CpuPowerSourceText.Text = snapshot.CpuPower.IsAvailable ? snapshot.CpuPower.Source : "Real sensor unavailable";
        CpuPowerText.ToolTip = snapshot.CpuPower.Diagnostic;
        CpuPowerSourceText.ToolTip = snapshot.CpuPower.Diagnostic;

        if (_testRunning)
        {
            if (IsValidTemperature(snapshot.Cpu.ValueCelsius))
            {
                double cpuTemperature = snapshot.Cpu.ValueCelsius.GetValueOrDefault();
                _peakCpuTemperature = !_peakCpuTemperature.HasValue
                    ? cpuTemperature
                    : Math.Max(_peakCpuTemperature.Value, cpuTemperature);
            }

            if (IsValidTemperature(gpuSafety.ValueCelsius))
            {
                double gpuTemperature = gpuSafety.ValueCelsius.GetValueOrDefault();
                _peakGpuTemperature = !_peakGpuTemperature.HasValue
                    ? gpuTemperature
                    : Math.Max(_peakGpuTemperature.Value, gpuTemperature);
            }
        }

        ApplyFans(snapshot.Fans);

        GpuPowerText.Text = snapshot.GpuPower.IsAvailable ? $"Power: {snapshot.GpuPower.Display}" : "Power: --";
        GpuLoadText.Text = snapshot.GpuLoad.IsAvailable ? snapshot.GpuLoad.Display : "--";
        GpuVramText.Text = snapshot.GpuVram.IsAvailable ? $"VRAM: {snapshot.GpuVram.Display}" : "VRAM: --";

        // Neutral colour when a sensor is missing; the temperature colour only when there is a reading.
        if (snapshot.Cpu.IsAvailable && IsValidTemperature(snapshot.Cpu.ValueCelsius))
        {
            CpuTemperatureText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
        }
        else
        {
            CpuTemperatureText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        }

        if (gpuSafety.IsAvailable && IsValidTemperature(gpuSafety.ValueCelsius))
        {
            GpuTemperatureText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE1, 0x1D, 0x48));
        }
        else
        {
            GpuTemperatureText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        }

        UpdateGauge(CpuTempGauge, snapshot.Cpu.IsAvailable ? snapshot.Cpu.ValueCelsius : null, CpuLimitSlider.Value);
        UpdateGauge(GpuTempGauge, gpuSafety.IsAvailable ? gpuSafety.ValueCelsius : null, GpuLimitSlider.Value);
        RecordChartSample(snapshot, gpuSafety);

        CpuPeakText.Text = _peakCpuTemperature.HasValue ? $"Peak: {_peakCpuTemperature.Value:0.0} °C" : "Peak: -- °C";
        GpuPeakText.Text = _peakGpuTemperature.HasValue
            ? gpuSafety.UsesSharedCpuSensor
                ? $"Shared peak: {_peakGpuTemperature.Value:0.0} °C"
                : $"Peak: {_peakGpuTemperature.Value:0.0} °C"
            : gpuSafety.UsesSharedCpuSensor ? "Shared CPU sensor" : "Peak: -- °C";

        string cpuDetail = snapshot.Cpu.IsAvailable
            ? $"CPU: {snapshot.Cpu.SensorName} ({snapshot.Cpu.Source})"
            : $"CPU: {snapshot.Cpu.Diagnostic}";
        string gpuDetail = gpuSafety.IsAvailable
            ? gpuSafety.UsesSharedCpuSensor
                ? $"GPU safety: shared CPU sensor for integrated graphics ({gpuSafety.SensorName})"
                : $"GPU: {gpuSafety.SensorName} ({gpuSafety.Source})"
            : $"GPU: {snapshot.Gpu.Diagnostic} {_graphicsThermalProfile.Diagnostic}";
        SensorDetailText.Text = $"{cpuDetail} · {gpuDetail}";
    }

    private async Task StopTestAsync(string reason, bool playAlert)
    {
        if (!_testRunning)
        {
            return;
        }

        _testRunning = false;
        _gpuComputeLoadController.Stop();
        _gpuRenderLoadController.Stop();
        await _cpuLoadGenerator.StopAsync();
        if (_closing)
        {
            return;
        }

        SetRunningState(false);
        UpdateTimeDisplay(finalUpdate: true);
        ActiveLoadText.Text = "Idle";
        WorkerDetailText.Text = "No load workers active";
        GpuPreviewPlaceholder.Visibility = Visibility.Visible;
        StatusText.Text = reason;
        StatusText.Foreground = GetThemeBrush(
            playAlert ? "DangerBrush" : "SuccessBrush",
            playAlert ? WpfBrushes.Red : WpfBrushes.ForestGreen);
        bool completed = reason.StartsWith("Completed", StringComparison.Ordinal);
        string outcome = playAlert ? "Safety stop" : completed ? "Completed" : "Stopped";
        SetState(outcome, playAlert ? "DangerBrush" : completed ? "SuccessBrush" : "WarningBrush");
        FinishResultTracking(outcome, reason, playAlert);

        if (playAlert)
        {
            SystemSounds.Exclamation.Play();
        }
    }

    private string? ValidateStart(
        ThermalTestMode mode,
        ThermalSensorSnapshot snapshot,
        double cpuLimit,
        double gpuLimit)
    {
        if (UsesCpu(mode))
        {
            if (!snapshot.Cpu.IsAvailable || !snapshot.Cpu.IsCpuSpecific || !IsValidTemperature(snapshot.Cpu.ValueCelsius))
            {
                return "A CPU-specific temperature sensor is required before the CPU load can start. " +
                    "Open HWiNFO Sensors with Shared Memory Support or install the included hardware-access dependency, then try again.";
            }

            double cpuTemperature = snapshot.Cpu.ValueCelsius.GetValueOrDefault();
            if (cpuTemperature >= cpuLimit)
            {
                return $"The CPU is already at {cpuTemperature:0.0} °C, which meets or exceeds the selected {cpuLimit:0} °C stop limit.";
            }
        }

        if (UsesGpu(mode))
        {
            if (!GpuRenderLoadController.IsHardwareRenderingAvailable)
            {
                return "Windows reports software-only WPF rendering. The GPU load will not start because it would load the CPU instead of reliably exercising the GPU.";
            }

            GpuSafetyReading gpuSafety = ResolveGpuSafetyReading(snapshot);
            if (!gpuSafety.IsAvailable || !IsValidTemperature(gpuSafety.ValueCelsius))
            {
                return "No safe GPU temperature path is available. Integrated-only graphics may use a CPU package sensor because the CPU and integrated GPU share the package. " +
                    "Dedicated, hybrid or unclassified graphics still require a real GPU temperature sensor. " +
                    _graphicsThermalProfile.Diagnostic;
            }

            double gpuTemperature = gpuSafety.ValueCelsius.GetValueOrDefault();
            if (gpuTemperature >= gpuLimit)
            {
                return gpuSafety.UsesSharedCpuSensor
                    ? $"The shared CPU package sensor is already at {gpuTemperature:0.0} °C, which meets or exceeds the selected {gpuLimit:0} °C GPU stop limit."
                    : $"The GPU is already at {gpuTemperature:0.0} °C, which meets or exceeds the selected {gpuLimit:0} °C stop limit.";
            }
        }

        return null;
    }

    private void UpdateTimeDisplay(bool finalUpdate = false)
    {
        if (!_testRunning && !finalUpdate)
        {
            return;
        }

        TimeSpan elapsed = DateTime.Now - _testStartedAt;
        if (_testDuration > TimeSpan.Zero)
        {
            elapsed = elapsed > _testDuration ? _testDuration : elapsed;
        }

        TimeSpan remaining = _testDuration > elapsed ? _testDuration - elapsed : TimeSpan.Zero;
        ElapsedText.Text = elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        RemainingText.Text = $"Remaining: {remaining:mm\\:ss}";
        TestProgressBar.Value = _testDuration.TotalSeconds <= 0
            ? 0
            : Math.Clamp(elapsed.TotalSeconds / _testDuration.TotalSeconds * 100.0, 0.0, 100.0);
    }

    private void SetRunningState(bool running)
    {
        ModeChoices.IsEnabled = !running;
        DurationChoices.IsEnabled = !running;
        LoadChoices.IsEnabled = !running;
        GpuAdapterComboBox.IsEnabled = !running && GpuAdapterComboBox.Items.Count > 0;
        SaveReportButton.IsEnabled = !running && _chartTimes.Count > 0;
        CpuLimitSlider.IsEnabled = !running && UsesCpu(GetSelectedMode());
        GpuLimitSlider.IsEnabled = !running && UsesGpu(GetSelectedMode());
        AcknowledgeRiskCheckBox.IsEnabled = !running;
        StartTestButton.IsEnabled = !running && AcknowledgeRiskCheckBox.IsChecked == true;
        StopTestButton.IsEnabled = running;
        StatusText.Foreground = GetThemeBrush("TextBrush", WpfBrushes.Black);
    }

    private void UpdateConfigurationAvailability()
    {
        if (CpuLimitSlider is null || GpuLimitSlider is null)
        {
            return;
        }

        ThermalTestMode mode = GetSelectedMode();
        CpuLimitSlider.IsEnabled = !_testRunning && UsesCpu(mode);
        GpuLimitSlider.IsEnabled = !_testRunning && UsesGpu(mode);
    }

    private void ModeChoice_Checked(object sender, RoutedEventArgs e)
    {
        UpdateConfigurationAvailability();
        UpdateChartThresholds();
    }

    private void TemperatureLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CpuLimitValueText is null || GpuLimitValueText is null)
        {
            return;
        }

        CpuLimitValueText.Text = $"{CpuLimitSlider.Value:0} °C";
        GpuLimitValueText.Text = $"{GpuLimitSlider.Value:0} °C";
        UpdateChartThresholds();
    }

    private void AcknowledgeRiskCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (StartTestButton is not null)
        {
            StartTestButton.IsEnabled = !_testRunning && AcknowledgeRiskCheckBox.IsChecked == true;
        }
    }

    private static string? CheckedTag(System.Windows.Controls.Panel? choices) =>
        choices?.Children.OfType<System.Windows.Controls.RadioButton>().FirstOrDefault(choice => choice.IsChecked == true)?.Tag as string;

    private static int GetSelectedInteger(System.Windows.Controls.Panel choices, int fallback) =>
        int.TryParse(CheckedTag(choices), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private ThermalTestMode GetSelectedMode()
    {
        string tag = CheckedTag(ModeChoices) ?? string.Empty;
        return tag switch
        {
            "Gpu" => ThermalTestMode.Gpu,
            "Combined" => ThermalTestMode.Combined,
            _ => ThermalTestMode.Cpu
        };
    }

    private string BuildWorkerDetail(ThermalTestMode mode)
    {
        return mode switch
        {
            ThermalTestMode.Cpu => $"{_cpuLoadGenerator.WorkerCount} CPU workers",
            ThermalTestMode.Gpu => $"WPF render tier {GpuRenderLoadController.RenderingTier}",
            _ => $"{_cpuLoadGenerator.WorkerCount} CPU workers + WPF render tier {GpuRenderLoadController.RenderingTier}"
        };
    }

    private static string GetModeDisplay(ThermalTestMode mode) => mode switch
    {
        ThermalTestMode.Cpu => "CPU",
        ThermalTestMode.Gpu => "GPU render",
        _ => "combined CPU + GPU"
    };

    private static bool UsesCpu(ThermalTestMode mode) =>
        mode is ThermalTestMode.Cpu or ThermalTestMode.Combined;

    private static bool UsesGpu(ThermalTestMode mode) =>
        mode is ThermalTestMode.Gpu or ThermalTestMode.Combined;

    private static bool IsValidTemperature(double? value) =>
        value.HasValue && double.IsFinite(value.Value) && value.Value is > 0 and <= 130;

    private GpuSafetyReading ResolveGpuSafetyReading(ThermalSensorSnapshot snapshot)
    {
        if (snapshot.Gpu.IsAvailable && IsValidTemperature(snapshot.Gpu.ValueCelsius))
        {
            return new GpuSafetyReading(
                true,
                snapshot.Gpu.ValueCelsius,
                false,
                snapshot.Gpu.SensorName,
                snapshot.Gpu.Source);
        }

        if (_graphicsThermalProfile.IsIntegratedOnly &&
            snapshot.Cpu.IsAvailable &&
            snapshot.Cpu.IsCpuSpecific &&
            IsValidTemperature(snapshot.Cpu.ValueCelsius))
        {
            return new GpuSafetyReading(
                true,
                snapshot.Cpu.ValueCelsius,
                true,
                snapshot.Cpu.SensorName,
                snapshot.Cpu.Source);
        }

        return new GpuSafetyReading(false, null, false, string.Empty, "None");
    }

    private static WpfBrush GetThemeBrush(string resourceKey, WpfBrush fallback) =>
        WpfApplication.Current.Resources[resourceKey] as WpfBrush ?? fallback;

    // ---------- live graph, gauges, state and results ----------

    private void SetState(string text, string brushKey)
    {
        StateText.Text = text;
        StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
        StateText.SetResourceReference(TextBlock.ForegroundProperty, brushKey == "MutedTextBrush" ? "TextBrush" : brushKey);
    }

    /// <summary>Bar showing how close a temperature is to its stop limit: green, then amber above 75 %, red above 90 %.</summary>
    private void UpdateGauge(System.Windows.Controls.ProgressBar gauge, double? temperature, double limit)
    {
        if (!IsValidTemperature(temperature) || limit <= 0)
        {
            gauge.Value = 0;
            return;
        }

        double fraction = Math.Clamp(temperature!.Value / limit, 0, 1);
        gauge.Value = fraction * 100;
        gauge.SetResourceReference(ForegroundProperty, fraction >= 0.9 ? "DangerBrush" : fraction >= 0.75 ? "WarningBrush" : "SuccessBrush");
    }

    private void UpdateChartThresholds()
    {
        if (CpuLimitSlider is null || GpuLimitSlider is null || ModeChoices is null)
        {
            return;
        }

        ThermalTestMode mode = _testRunning ? _activeMode : GetSelectedMode();
        _cpuTempSeries.Threshold = UsesCpu(mode) ? (_testRunning ? _cpuStopTemperature : CpuLimitSlider.Value) : null;
        _gpuTempSeries.Threshold = UsesGpu(mode) ? (_testRunning ? _gpuStopTemperature : GpuLimitSlider.Value) : null;
        RedrawChart();
    }

    /// <summary>Shows the whole test (plus a little before and after); otherwise the last two minutes.</summary>
    private void RedrawChart()
    {
        // Sliders raise ValueChanged while InitializeComponent is still creating the chart below them.
        if (ThermalChart is null)
        {
            return;
        }

        TimeSpan range = TimeSpan.FromMinutes(2);
        if (_testRunning || _testEndedAt.HasValue)
        {
            DateTime from = _testStartedAt - TimeSpan.FromSeconds(20);
            DateTime to = _testRunning ? _testStartedAt + _testDuration : _testEndedAt!.Value;
            DateTime now = _chartTimes.Count > 0 ? _chartTimes[^1] : DateTime.Now;
            range = (now > to ? now : to) - from + TimeSpan.FromSeconds(10);
            range = range < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : range;
        }

        ThermalChart.SetData(_chartTimes, AllSeries, range);
        ThermalChart.SetMarkers(_chartMarkers);
    }

    private void RecordChartSample(ThermalSensorSnapshot snapshot, GpuSafetyReading gpuSafety)
    {
        double? fan = snapshot.Fans.IsAvailable
            ? snapshot.Fans.Fans.Where(item => item.Group == FanGroup.Cpu).Select(item => (double?)item.Rpm).DefaultIfEmpty(null).Max()
            : null;

        _chartTimes.Add(DateTime.Now);
        _cpuTempSeries.Values.Add(snapshot.Cpu.IsAvailable && IsValidTemperature(snapshot.Cpu.ValueCelsius) ? snapshot.Cpu.ValueCelsius : null);
        _gpuTempSeries.Values.Add(snapshot.Gpu.IsAvailable && IsValidTemperature(snapshot.Gpu.ValueCelsius) ? snapshot.Gpu.ValueCelsius : null);
        _cpuLoadSeries.Values.Add(snapshot.CpuUsagePercent);
        _cpuPowerSeries.Values.Add(snapshot.CpuPower.IsAvailable ? snapshot.CpuPower.Watts : null);
        _cpuClockSeries.Values.Add(snapshot.CpuClock.IsAvailable && snapshot.CpuClock.Megahertz.HasValue ? snapshot.CpuClock.Megahertz.Value / 1000.0 : null);
        _gpuLoadSeries.Values.Add(snapshot.GpuLoad.IsAvailable ? snapshot.GpuLoad.LoadPercent : null);
        _fanSeries.Values.Add(fan);

        if (_chartTimes.Count > MaxChartSamples)
        {
            _chartTimes.RemoveAt(0);
            foreach (LiveChartSeries series in AllSeries)
            {
                series.Values.RemoveAt(0);
            }
        }

        if (_testRunning)
        {
            if (snapshot.CpuPower.IsAvailable && snapshot.CpuPower.Watts is double watts)
            {
                _powerTotal += watts;
                _powerSamples++;
                _powerMax = _powerMax is null ? watts : Math.Max(_powerMax.Value, watts);
            }

            if (fan.HasValue)
            {
                _maxFanRpm = _maxFanRpm is null ? fan : Math.Max(_maxFanRpm.Value, fan.Value);
            }
        }

        SaveReportButton.IsEnabled = !_testRunning;
        RedrawChart();
    }

    private void BeginResultTracking(ThermalSensorSnapshot snapshot, GpuSafetyReading gpuSafety, int loadPercent)
    {
        _activeLoadPercent = loadPercent;
        _testEndedAt = null;
        _startCpuTemperature = IsValidTemperature(snapshot.Cpu.ValueCelsius) ? snapshot.Cpu.ValueCelsius : null;
        _startGpuTemperature = IsValidTemperature(gpuSafety.ValueCelsius) ? gpuSafety.ValueCelsius : null;
        _startFanRpm = snapshot.Fans.IsAvailable
            ? snapshot.Fans.Fans.Where(item => item.Group == FanGroup.Cpu).Select(item => (double?)item.Rpm).DefaultIfEmpty(null).Max()
            : null;
        _maxFanRpm = _startFanRpm;
        _powerTotal = 0;
        _powerSamples = 0;
        _powerMax = null;
        _chartMarkers.Add((DateTime.Now, $"Start: {GetModeDisplay(_activeMode)} {loadPercent}%"));
        ResultPanel.Visibility = Visibility.Collapsed;
        ChartHintText.Text = "Test running. The red dashed lines are the stop limits; the test stops itself if a temperature reaches them.";
        UpdateChartThresholds();
    }

    private void FinishResultTracking(string outcome, string reason, bool safetyStop)
    {
        _testEndedAt = DateTime.Now;
        _chartMarkers.Add((_testEndedAt.Value, outcome));
        TimeSpan ran = _testEndedAt.Value - _testStartedAt;
        ran = ran > _testDuration ? _testDuration : ran;

        List<string> lines = new()
        {
            $"{GetModeDisplay(_activeMode)} load at {_activeLoadPercent}% for {ran:mm\\:ss} of {_testDuration:mm\\:ss}.  {reason}"
        };

        if (UsesCpu(_activeMode))
        {
            lines.Add(TemperatureLine("CPU", _startCpuTemperature, _peakCpuTemperature, _cpuStopTemperature));
        }

        if (UsesGpu(_activeMode))
        {
            lines.Add(TemperatureLine("GPU", _startGpuTemperature, _peakGpuTemperature, _gpuStopTemperature));
        }

        if (_powerSamples > 0)
        {
            lines.Add($"CPU package power: average {_powerTotal / _powerSamples:0.0} W, maximum {_powerMax:0.0} W.");
        }

        lines.Add(_startFanRpm.HasValue || _maxFanRpm.HasValue
            ? $"CPU fan: {_startFanRpm:0} RPM at start, up to {_maxFanRpm:0} RPM during the test."
            : "CPU fan speed is not exposed by this PC, so fan response cannot be shown.");

        ResultTitleText.Text = $"Test result - {outcome}";
        ResultTitleText.SetResourceReference(TextBlock.ForegroundProperty, safetyStop ? "DangerBrush" : outcome == "Completed" ? "SuccessBrush" : "WarningBrush");
        ResultText.Text = string.Join(Environment.NewLine, lines);
        ResultPanel.Visibility = Visibility.Visible;
        ChartHintText.Text = "Test finished. Click any point to pin it; Save report exports every sample as a CSV file.";
        SaveReportButton.IsEnabled = true;
        UpdateChartThresholds();
    }

    private static string TemperatureLine(string name, double? start, double? peak, double limit)
    {
        if (!peak.HasValue)
        {
            return $"{name} temperature: no reading.";
        }

        string rise = start.HasValue ? $" (+{peak.Value - start.Value:0.0} °C from {start.Value:0.0} °C)" : string.Empty;
        return $"{name} temperature: peak {peak.Value:0.0} °C{rise}, {limit - peak.Value:0.0} °C below the {limit:0} °C limit.";
    }

    /// <summary>Every recorded sample plus the result summary, as CSV for spreadsheets or a support ticket.</summary>
    private void SaveReportButton_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = "Save thermal test report",
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"thermal-test-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            System.Text.StringBuilder csv = new();
            if (ResultPanel.Visibility == Visibility.Visible)
            {
                foreach (string line in ResultText.Text.Split(Environment.NewLine))
                {
                    csv.AppendLine("# " + line);
                }
            }

            csv.AppendLine("Time," + string.Join(",", AllSeries.Select(series => $"{series.Name} ({series.Unit})")));
            for (int index = 0; index < _chartTimes.Count; index++)
            {
                csv.Append(_chartTimes[index].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                foreach (LiveChartSeries series in AllSeries)
                {
                    csv.Append(',');
                    if (series.Values.ElementAtOrDefault(index) is double value)
                    {
                        csv.Append(value.ToString("0.##", CultureInfo.InvariantCulture));
                    }
                }

                csv.AppendLine();
            }

            System.IO.File.WriteAllText(dialog.FileName, csv.ToString(), new System.Text.UTF8Encoding(true));
            StatusText.Text = $"Report saved to {dialog.FileName}";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save thermal report", exception);
            WpfMessageBox.Show(this, exception.Message, "Save report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        _monitorTimer.Stop();
        _gpuComputeLoadController.Dispose();
        _gpuRenderLoadController.Dispose();
        _cpuLoadGenerator.Dispose();
        _hardwareMonitorService.Dispose();
        base.OnClosing(e);
    }

    private enum ThermalTestMode
    {
        Cpu,
        Gpu,
        Combined
    }

    private sealed record ThermalSensorSnapshot(
        CpuTemperatureReading Cpu,
        GpuTemperatureReading Gpu,
        CpuClockReading CpuClock,
        CpuPackagePowerReading CpuPower,
        FanInventoryReading Fans,
        GpuPowerReading GpuPower,
        GpuUtilisationReading GpuLoad,
        GpuMemoryReading GpuVram,
        double? CpuUsagePercent);

    private sealed record GpuSafetyReading(
        bool IsAvailable,
        double? ValueCelsius,
        bool UsesSharedCpuSensor,
        string SensorName,
        string Source);

    /// <summary>
    /// Fills the adapter list from DXGI so discrete and integrated graphics can be tested
    /// separately. The WPF render load could never do this - it runs on whichever adapter
    /// Windows hands WPF.
    /// </summary>
    private void PopulateGpuAdapters()
    {
        try
        {
            IReadOnlyList<DxgiAdapterInfo> adapters = DxgiAdapterService.GetAdapters()
                .Where(adapter => !adapter.IsSoftwareAdapter)
                .ToList();

            GpuAdapterComboBox.Items.Clear();
            foreach (DxgiAdapterInfo adapter in adapters)
            {
                string kind = adapter.IsDiscrete ? "Dedicated" : "Integrated";
                GpuAdapterComboBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{adapter.Description} ({kind})",
                    Tag = adapter.Description
                });
            }

            if (GpuAdapterComboBox.Items.Count > 0)
            {
                GpuAdapterComboBox.SelectedIndex = 0;
                GpuLoadModeText.Text = adapters.Count > 1
                    ? "Direct3D 11 compute load. Choose which adapter to heat."
                    : "Direct3D 11 compute load on the only hardware adapter present.";
            }
            else
            {
                GpuAdapterComboBox.IsEnabled = false;
                GpuLoadModeText.Text = "No hardware graphics adapter was detected.";
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Populate GPU adapter list", exception);
            GpuAdapterComboBox.IsEnabled = false;
            GpuLoadModeText.Text = "Graphics adapters could not be listed.";
        }
    }

    private string? SelectedAdapterDescription() =>
        GpuAdapterComboBox.SelectedItem is ComboBoxItem item ? item.Tag as string : null;

}
