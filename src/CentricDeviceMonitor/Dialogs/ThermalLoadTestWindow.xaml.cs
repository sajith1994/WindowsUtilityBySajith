using System.ComponentModel;
using System.Globalization;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CentricDeviceMonitor.Services;
using WpfApplication = System.Windows.Application;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor.Dialogs;

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

    public ThermalLoadTestWindow()
    {
        InitializeComponent();
        _gpuRenderLoadController = new GpuRenderLoadController(GpuLoadViewport);
        PopulateGpuAdapters();
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
        int durationSeconds = GetSelectedInteger(DurationComboBox, 60);
        int loadPercent = GetSelectedInteger(LoadLevelComboBox, 100);
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
            SetRunningState(true);
            TestProgressBar.Value = 0;
            StatusText.Text = "Thermal load is running. Keep the system supervised.";
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
            _hardwareMonitorService.GetGpuMemoryReading()));
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
        GpuLoadText.Text = snapshot.GpuLoad.IsAvailable ? $"Load: {snapshot.GpuLoad.Display}" : "Load: --";
        GpuVramText.Text = snapshot.GpuVram.IsAvailable ? $"VRAM: {snapshot.GpuVram.Display}" : "VRAM: --";

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
        TestModeComboBox.IsEnabled = !running;
        DurationComboBox.IsEnabled = !running;
        LoadLevelComboBox.IsEnabled = !running;
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

    private void TestModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateConfigurationAvailability();
    }

    private void TemperatureLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CpuLimitLabel is null || GpuLimitLabel is null)
        {
            return;
        }

        CpuLimitLabel.Text = $"CPU stop temperature: {CpuLimitSlider.Value:0} °C";
        GpuLimitLabel.Text = $"GPU stop temperature: {GpuLimitSlider.Value:0} °C";
    }

    private void AcknowledgeRiskCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (StartTestButton is not null)
        {
            StartTestButton.IsEnabled = !_testRunning && AcknowledgeRiskCheckBox.IsChecked == true;
        }
    }

    private static int GetSelectedInteger(WpfComboBox comboBox, int fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item &&
               int.TryParse(Convert.ToString(item.Tag, CultureInfo.InvariantCulture), out int value)
            ? value
            : fallback;
    }

    private ThermalTestMode GetSelectedMode()
    {
        string tag = TestModeComboBox?.SelectedItem is ComboBoxItem item
            ? Convert.ToString(item.Tag, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;
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
        GpuMemoryReading GpuVram);

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
