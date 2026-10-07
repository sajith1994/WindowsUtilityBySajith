using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using Hwinfo.SharedMemory;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using HwiNfoSensorType = Hwinfo.SharedMemory.SensorType;
using LibreSensorType = LibreHardwareMonitor.Hardware.SensorType;

namespace CentricDeviceMonitor.Services;

public sealed record CpuTemperatureReading(
    bool IsAvailable,
    double? ValueCelsius,
    string Display,
    string SensorName,
    string Diagnostic,
    string Source,
    bool IsCpuSpecific);

public sealed record CpuFanReading(
    bool IsAvailable,
    double? Rpm,
    string Display,
    string SensorName,
    string Source,
    string Diagnostic);

public sealed record CpuPackagePowerReading(
    bool IsAvailable,
    double? Watts,
    string Display,
    string SensorName,
    string Source,
    string Diagnostic);

public sealed record CpuClockReading(
    bool IsAvailable,
    double? Megahertz,
    string Display,
    string SensorName,
    string Source,
    string Diagnostic);

public sealed record GraphicsThermalProfile(
    bool IsIntegratedOnly,
    string AdapterDisplay,
    string Diagnostic);

public sealed record GpuPowerReading(
    bool IsAvailable,
    double? Watts,
    string Display,
    string SensorName,
    string Source,
    string Diagnostic);

public sealed record GpuTemperatureReading(
    bool IsAvailable,
    double? ValueCelsius,
    string Display,
    string SensorName,
    string Source,
    string Diagnostic);

/// <summary>Which physical area a fan belongs to, so the dashboard can group it.</summary>
public enum FanGroup
{
    Cpu,
    System,
    Gpu
}

public sealed record FanSensorReading(
    string Name,
    double Rpm,
    FanGroup Group);

/// <summary>
/// Every fan the hardware library can see, rather than the single best-ranked one.
/// A desktop typically has a CPU fan, one or more case fans and several GPU fans.
/// </summary>
public sealed record FanInventoryReading(
    bool IsAvailable,
    IReadOnlyList<FanSensorReading> Fans,
    string Source,
    string Diagnostic)
{
    public IEnumerable<FanSensorReading> CpuAndSystemFans =>
        Fans.Where(fan => fan.Group is FanGroup.Cpu or FanGroup.System);

    public IEnumerable<FanSensorReading> GpuFans => Fans.Where(fan => fan.Group == FanGroup.Gpu);
}

public sealed record GpuUtilisationReading(
    bool IsAvailable,
    double? LoadPercent,
    string Display,
    string SensorName,
    string Source,
    string Diagnostic);

public sealed record GpuMemoryReading(
    bool IsAvailable,
    double? UsedMegabytes,
    double? TotalMegabytes,
    string Display,
    string SensorName,
    string Source,
    string Diagnostic);

public sealed class HardwareMonitorService : IDisposable
{
    private static readonly TimeSpan FallbackCacheDuration = TimeSpan.FromSeconds(15);

    private readonly object _syncRoot = new();
    private Computer? _computer;
    private string _openError = string.Empty;
    private string _hwiNfoDiagnostic =
        "HWiNFO shared memory has not been detected yet.";
    private bool _disposed;
    private DateTime _lastFallbackAttemptUtc = DateTime.MinValue;
    private CpuTemperatureReading? _lastFallbackReading;

    public HardwareMonitorService()
    {
        InitializeComputer();
    }

    public CpuTemperatureReading GetCpuTemperatureReading()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return Unavailable("The hardware monitor has already been closed.");
            }

            CpuTemperatureReading? directReading = TryReadLibreHardwareMonitor();
            if (directReading is not null)
            {
                return directReading;
            }

            // HWiNFO is checked on every refresh so starting HWiNFO or enabling
            // Shared Memory Support is detected without waiting for the WMI cache.
            CpuTemperatureReading? hwiNfoReading = TryReadHwiNfoSharedMemory();
            if (hwiNfoReading is not null)
            {
                return hwiNfoReading;
            }

            if (_lastFallbackReading is not null &&
                DateTime.UtcNow - _lastFallbackAttemptUtc < FallbackCacheDuration)
            {
                return _lastFallbackReading;
            }

            _lastFallbackAttemptUtc = DateTime.UtcNow;
            _lastFallbackReading =
                TryReadHardwareMonitorWmiProvider(
                    @"\\.\root\LibreHardwareMonitor",
                    "LibreHardwareMonitor WMI") ??
                TryReadHardwareMonitorWmiProvider(
                    @"\\.\root\OpenHardwareMonitor",
                    "OpenHardwareMonitor WMI") ??
                TryReadAcpiThermalZoneDiagnostic() ??
                Unavailable(BuildUnavailableDiagnostic());

            return _lastFallbackReading;
        }
    }

    public CpuFanReading GetCpuFanReading()
    {
        lock (_syncRoot)
        {
            if (_disposed || _computer is null)
            {
                return FanUnavailable("The hardware monitor is not open.");
            }

            try
            {
                // LibreHardwareMonitor's documented integration pattern updates the complete
                // hardware tree with a visitor. This is important for embedded-controller and
                // sub-hardware fan sensors used by many laptops.
                _computer.Accept(new UpdateVisitor());

                List<FanCandidate> candidates = new();
                List<FanControlCandidate> controls = new();
                foreach (IHardware hardware in _computer.Hardware)
                {
                    CollectFanSensors(hardware, candidates, controls);
                }

                FanCandidate? selected = candidates
                    .OrderByDescending(candidate => candidate.Priority)
                    .ThenByDescending(candidate => candidate.Rpm)
                    .FirstOrDefault();

                if (selected is not null)
                {
                    return new CpuFanReading(
                        true,
                        selected.Rpm,
                        $"Fan: {selected.Rpm:0} RPM",
                        selected.SensorName,
                        IsPawnIoInstalled() ? "LibreHardwareMonitor + PawnIO" : "LibreHardwareMonitor",
                        $"Detected {candidates.Count} RPM fan sensor(s) in the LibreHardwareMonitor hardware tree.");
                }

                CpuFanReading? wmiReading =
                    TryReadFanFromHardwareMonitorWmiProvider(@"\\.\root\LibreHardwareMonitor", "LibreHardwareMonitor WMI") ??
                    TryReadFanFromHardwareMonitorWmiProvider(@"\\.\root\OpenHardwareMonitor", "OpenHardwareMonitor WMI");

                if (wmiReading is not null)
                {
                    return wmiReading;
                }

                FanControlCandidate? control = controls
                    .OrderByDescending(candidate => candidate.Priority)
                    .ThenByDescending(candidate => candidate.Percent)
                    .FirstOrDefault();

                if (control is not null)
                {
                    return new CpuFanReading(
                        false,
                        null,
                        $"Fan control: {control.Percent:0}% (RPM unavailable)",
                        control.SensorName,
                        IsPawnIoInstalled() ? "LibreHardwareMonitor + PawnIO" : "LibreHardwareMonitor",
                        $"Fan RPM is not exposed by the laptop firmware, but a fan-control sensor was found: " +
                        $"{control.SensorName} at {control.Percent:0}%.");
                }

                return FanUnavailable(
                    "No RPM fan sensor was exposed by LibreHardwareMonitor, its WMI provider, or OpenHardwareMonitor. " +
                    "Many laptops keep fan tachometer data inside OEM embedded-controller firmware and do not expose it to Windows. " +
                    "Use Fan diagnostics to see every fan/control sensor the hardware library can access.");
            }
            catch (Exception exception)
            {
                return FanUnavailable($"Fan sensor scan failed: {exception.Message}");
            }
        }
    }

    public CpuPackagePowerReading GetCpuPackagePowerReading()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return PowerUnavailable("The hardware monitor has already been closed.");
            }

            CpuPackagePowerReading? directReading = TryReadLibreHardwareMonitorCpuPackagePower();
            if (directReading is not null)
            {
                return directReading;
            }

            return
                TryReadPowerFromHardwareMonitorWmiProvider(
                    @"\\.\root\LibreHardwareMonitor",
                    "LibreHardwareMonitor WMI") ??
                TryReadPowerFromHardwareMonitorWmiProvider(
                    @"\\.\root\OpenHardwareMonitor",
                    "OpenHardwareMonitor WMI") ??
                PowerUnavailable(
                    "CPU package power is not exposed by the available hardware sensors on this system. " +
                    "Package power is read from a real CPU Power sensor when LibreHardwareMonitor exposes one; it is not estimated from CPU usage.");
        }
    }

    public CpuClockReading GetCpuClockReading()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return ClockUnavailable("The hardware monitor has already been closed.");
            }

            CpuClockReading? directReading = TryReadLibreHardwareMonitorCpuClock();
            if (directReading is not null)
            {
                return directReading;
            }

            return TryReadWindowsCpuClock() ?? ClockUnavailable(
                "CPU running speed is not exposed by LibreHardwareMonitor or Win32_Processor on this system.");
        }
    }

    public static GraphicsThermalProfile GetGraphicsThermalProfile()
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                "SELECT Name, Status FROM Win32_VideoController");
            List<(string Name, GraphicsAdapterKind Kind)> adapters = new();
            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                string name = Convert.ToString(item["Name"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name) || IsSoftwareDisplayAdapter(name))
                {
                    continue;
                }

                adapters.Add((name, ClassifyGraphicsAdapter(name)));
            }

            string adapterDisplay = adapters.Count == 0
                ? "Graphics adapter not identified"
                : string.Join(" + ", adapters.Select(adapter => adapter.Name));
            bool integratedOnly = adapters.Count > 0 &&
                adapters.All(adapter => adapter.Kind == GraphicsAdapterKind.Integrated);

            string diagnostic = integratedOnly
                ? $"Integrated-only graphics detected: {adapterDisplay}. The CPU package temperature may be used as the shared thermal safety sensor when no separate GPU sensor exists."
                : $"A dedicated, hybrid or unclassified graphics configuration was detected: {adapterDisplay}. A real GPU temperature sensor remains required.";
            return new GraphicsThermalProfile(integratedOnly, adapterDisplay, diagnostic);
        }
        catch (Exception exception)
        {
            return new GraphicsThermalProfile(
                false,
                "Graphics adapter detection unavailable",
                $"Graphics adapter detection failed: {exception.Message}. A real GPU temperature sensor remains required.");
        }
    }

    public GpuPowerReading GetGpuPowerReading()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return GpuPowerUnavailable("The hardware monitor has already been closed.");
            }

            GpuPowerReading? directReading = TryReadLibreHardwareMonitorGpuPower();
            if (directReading is not null)
            {
                return directReading;
            }

            return
                TryReadGpuPowerFromHardwareMonitorWmiProvider(
                    @"\\.\root\LibreHardwareMonitor",
                    "LibreHardwareMonitor WMI") ??
                TryReadGpuPowerFromHardwareMonitorWmiProvider(
                    @"\\.\root\OpenHardwareMonitor",
                    "OpenHardwareMonitor WMI") ??
                GpuPowerUnavailable(
                    "GPU power is not exposed by the available hardware sensors on this system. " +
                    "Windows Utility reports a real GPU power sensor when LibreHardwareMonitor exposes one; it is not estimated from GPU utilization.");
        }
    }

    public GpuTemperatureReading GetGpuTemperatureReading()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return GpuTemperatureUnavailable("The hardware monitor has already been closed.");
            }

            GpuTemperatureReading? directReading = TryReadLibreHardwareMonitorGpuTemperature();
            if (directReading is not null)
            {
                return directReading;
            }

            return
                TryReadGpuTemperatureFromHardwareMonitorWmiProvider(
                    @"\\.\root\LibreHardwareMonitor",
                    "LibreHardwareMonitor WMI") ??
                TryReadGpuTemperatureFromHardwareMonitorWmiProvider(
                    @"\\.\root\OpenHardwareMonitor",
                    "OpenHardwareMonitor WMI") ??
                GpuTemperatureUnavailable(
                    "GPU temperature is not exposed by the available hardware sensors on this system. " +
                    "Dedicated and hybrid GPU thermal tests require a real GPU temperature sensor. The thermal-test window may use a CPU package sensor only for a conservatively identified integrated-only graphics configuration.");
        }
    }


    /// <summary>
    /// Returns every fan sensor, grouped by area, instead of only the highest-ranked one.
    /// The dashboard shows CPU and case fans on the CPU card and GPU fans on the GPU card.
    /// </summary>
    public FanInventoryReading GetFanInventoryReading()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return new FanInventoryReading(false, Array.Empty<FanSensorReading>(), "Unavailable", "The hardware monitor has already been closed.");
            }

            try
            {
                Computer? computer = _computer;
                if (computer is null)
                {
                    return new FanInventoryReading(false, Array.Empty<FanSensorReading>(), "Unavailable", _openError);
                }

                List<FanSensorReading> fans = new();
                foreach (IHardware hardware in computer.Hardware)
                {
                    CollectGroupedFans(hardware, fans);
                }

                // Deduplicate: the same physical fan can appear under both the motherboard and
                // its embedded controller sub-hardware.
                List<FanSensorReading> distinct = fans
                    .GroupBy(fan => fan.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(fan => fan.Group)
                    .ThenBy(fan => fan.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Super I/O chips name their headers generically ("ITE IT8686E - Fan #1"), so no
                // name match identifies the CPU header and every motherboard fan falls into the
                // System group. Reuse the ranked CPU fan selection so the CPU card and this
                // inventory agree on which fan is the CPU one.
                if (distinct.Count > 0 && !distinct.Any(fan => fan.Group == FanGroup.Cpu))
                {
                    // lock is reentrant, so calling the public reader from inside the lock is safe.
                    CpuFanReading cpuFan = GetCpuFanReading();
                    if (cpuFan.IsAvailable && !string.IsNullOrWhiteSpace(cpuFan.SensorName))
                    {
                        for (int index = 0; index < distinct.Count; index++)
                        {
                            if (distinct[index].Group == FanGroup.System &&
                                distinct[index].Name.Equals(cpuFan.SensorName, StringComparison.OrdinalIgnoreCase))
                            {
                                distinct[index] = distinct[index] with { Group = FanGroup.Cpu };
                                break;
                            }
                        }
                    }
                }

                if (distinct.Count == 0)
                {
                    return new FanInventoryReading(
                        false,
                        Array.Empty<FanSensorReading>(),
                        "LibreHardwareMonitor",
                        "No fan RPM sensors were exposed. Use Fan diagnostics to see every sensor the hardware library can access.");
                }

                return new FanInventoryReading(
                    true,
                    distinct,
                    "LibreHardwareMonitor + PawnIO",
                    $"{distinct.Count} fan sensor(s) detected.");
            }
            catch (Exception exception)
            {
                return new FanInventoryReading(false, Array.Empty<FanSensorReading>(), "Unavailable", $"Fan sensor scan failed: {exception.Message}");
            }
        }
    }

    public GpuUtilisationReading GetGpuUtilisationReading()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return new GpuUtilisationReading(false, null, "Unavailable", "Unknown", "Unavailable", "The hardware monitor has already been closed.");
            }

            try
            {
                Computer? computer = _computer;
                if (computer is null)
                {
                    return new GpuUtilisationReading(false, null, "Unavailable", "Unknown", "Unavailable", _openError);
                }

                List<SensorCandidate> candidates = new();
                foreach (IHardware hardware in computer.Hardware)
                {
                    CollectGpuLoadSensors(hardware, candidates);
                }

                SensorCandidate? selected = candidates
                    .OrderByDescending(candidate => candidate.Priority)
                    .FirstOrDefault();

                if (selected is null)
                {
                    return new GpuUtilisationReading(
                        false, null, "Unavailable", "Unknown", "LibreHardwareMonitor",
                        "No GPU core load sensor was exposed by the graphics driver.");
                }

                return new GpuUtilisationReading(
                    true,
                    selected.Value,
                    $"{selected.Value:0.#}%",
                    selected.SensorName,
                    "LibreHardwareMonitor + PawnIO",
                    "GPU core utilisation reported by the graphics driver.");
            }
            catch (Exception exception)
            {
                return new GpuUtilisationReading(false, null, "Unavailable", "Unknown", "Unavailable", $"GPU load scan failed: {exception.Message}");
            }
        }
    }

    public GpuMemoryReading GetGpuMemoryReading()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return new GpuMemoryReading(false, null, null, "Unavailable", "Unknown", "Unavailable", "The hardware monitor has already been closed.");
            }

            try
            {
                Computer? computer = _computer;
                if (computer is null)
                {
                    return new GpuMemoryReading(false, null, null, "Unavailable", "Unknown", "Unavailable", _openError);
                }

                double? used = null;
                double? total = null;
                string sensorName = "Unknown";

                foreach (IHardware hardware in computer.Hardware)
                {
                    CollectGpuMemorySensors(hardware, ref used, ref total, ref sensorName);
                }

                if (used is null && total is null)
                {
                    return new GpuMemoryReading(
                        false, null, null, "Unavailable", "Unknown", "LibreHardwareMonitor",
                        "No dedicated GPU memory sensor was exposed by the graphics driver.");
                }

                string display = used.HasValue && total.HasValue
                    ? $"{used.Value / 1024:0.0} / {total.Value / 1024:0.0} GB"
                    : used.HasValue
                        ? $"{used.Value / 1024:0.0} GB used"
                        : $"{(total ?? 0) / 1024:0.0} GB total";

                return new GpuMemoryReading(
                    true,
                    used,
                    total,
                    display,
                    sensorName,
                    "LibreHardwareMonitor + PawnIO",
                    "Dedicated video memory reported by the graphics driver.");
            }
            catch (Exception exception)
            {
                return new GpuMemoryReading(false, null, null, "Unavailable", "Unknown", "Unavailable", $"GPU memory scan failed: {exception.Message}");
            }
        }
    }

    private static bool IsGpuHardware(IHardware hardware) =>
        hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel ||
        ContainsAny(hardware.Name, "gpu", "graphics", "geforce", "radeon", "arc");

    private static void CollectGroupedFans(IHardware hardware, ICollection<FanSensorReading> fans)
    {
        try
        {
            hardware.Update();
        }
        catch
        {
            // Continue scanning the rest of the hardware tree.
        }

        bool gpuHardware = IsGpuHardware(hardware);

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType != LibreSensorType.Fan || !sensor.Value.HasValue)
            {
                continue;
            }

            double rpm = sensor.Value.Value;
            if (!double.IsFinite(rpm) || rpm is < 0 or > 30000)
            {
                continue;
            }

            string combined = $"{hardware.Name} {sensor.Name}";
            FanGroup group = gpuHardware || ContainsAny(combined, "gpu", "graphics", "geforce", "radeon")
                ? FanGroup.Gpu
                : ContainsAny(combined, "cpu", "processor")
                    ? FanGroup.Cpu
                    : FanGroup.System;

            fans.Add(new FanSensorReading($"{hardware.Name} - {sensor.Name}", rpm, group));
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            CollectGroupedFans(subHardware, fans);
        }
    }

    private static void CollectGpuLoadSensors(IHardware hardware, ICollection<SensorCandidate> candidates)
    {
        try
        {
            hardware.Update();
        }
        catch
        {
            // Continue scanning the rest of the hardware tree.
        }

        bool gpuHardware = IsGpuHardware(hardware);

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (!gpuHardware || sensor.SensorType != LibreSensorType.Load || !sensor.Value.HasValue)
            {
                continue;
            }

            double percent = sensor.Value.Value;
            if (!double.IsFinite(percent) || percent is < 0 or > 100)
            {
                continue;
            }

            // "GPU Core" is the figure Task Manager and MSI Afterburner show; the rest
            // (memory controller, video engine, bus) are supporting detail.
            int priority = ContainsAny(sensor.Name, "gpu core", "3d")
                ? 170
                : ContainsAny(sensor.Name, "memory controller", "video engine", "bus")
                    ? 90
                    : 120;

            candidates.Add(new SensorCandidate(percent, $"{hardware.Name} - {sensor.Name}", priority));
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            CollectGpuLoadSensors(subHardware, candidates);
        }
    }

    private static void CollectGpuMemorySensors(
        IHardware hardware,
        ref double? used,
        ref double? total,
        ref string sensorName)
    {
        try
        {
            hardware.Update();
        }
        catch
        {
            // Continue scanning the rest of the hardware tree.
        }

        if (IsGpuHardware(hardware))
        {
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.SensorType != LibreSensorType.SmallData || !sensor.Value.HasValue)
                {
                    continue;
                }

                double megabytes = sensor.Value.Value;
                if (!double.IsFinite(megabytes) || megabytes < 0)
                {
                    continue;
                }

                // Prefer the dedicated figures; shared/dynamic memory is system RAM, not VRAM.
                if (used is null && ContainsAny(sensor.Name, "memory used") && !ContainsAny(sensor.Name, "shared"))
                {
                    used = megabytes;
                    sensorName = $"{hardware.Name} - {sensor.Name}";
                }
                else if (total is null && ContainsAny(sensor.Name, "memory total") && !ContainsAny(sensor.Name, "shared"))
                {
                    total = megabytes;
                }
            }
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            CollectGpuMemorySensors(subHardware, ref used, ref total, ref sensorName);
        }
    }

    public string GetFanDiagnosticReport()    {
        lock (_syncRoot)
        {
            StringBuilder report = new();
            report.AppendLine("Windows Utility by Sajith - Fan diagnostics");
            report.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"PawnIO: {GetPawnIoDiagnostic()}");
            report.AppendLine();

            if (_disposed || _computer is null)
            {
                report.AppendLine("LibreHardwareMonitor is not open.");
                if (!string.IsNullOrWhiteSpace(_openError))
                {
                    report.AppendLine($"Open error: {_openError}");
                }
                return report.ToString();
            }

            try
            {
                _computer.Accept(new UpdateVisitor());
                int fanCount = 0;
                int controlCount = 0;

                foreach (IHardware hardware in _computer.Hardware)
                {
                    AppendFanDiagnostics(hardware, report, 0, ref fanCount, ref controlCount);
                }

                report.AppendLine();
                report.AppendLine($"RPM fan sensors found: {fanCount}");
                report.AppendLine($"Fan/control percentage sensors found: {controlCount}");
                if (fanCount == 0)
                {
                    report.AppendLine();
                    report.AppendLine("No RPM sensor is available to LibreHardwareMonitor on this machine.");
                    report.AppendLine("This normally means the laptop manufacturer does not expose fan tachometer data through a supported EC/Super-I/O interface.");
                }
            }
            catch (Exception exception)
            {
                report.AppendLine($"Diagnostic scan failed: {exception}");
            }

            return report.ToString();
        }
    }

    public void Rescan()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            CloseComputer();
            _lastFallbackAttemptUtc = DateTime.MinValue;
            _lastFallbackReading = null;
            _hwiNfoDiagnostic = "HWiNFO shared memory is being checked.";
            InitializeComputer();
        }
    }

    private void InitializeComputer()
    {
        _openError = string.Empty;

        try
        {
            Computer computer = new()
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true
            };

            computer.Open();
            computer.Accept(new UpdateVisitor());
            _computer = computer;
        }
        catch (Exception exception)
        {
            _computer = null;
            _openError = exception.Message;
        }
    }

    private CpuTemperatureReading? TryReadLibreHardwareMonitor()
    {
        if (_computer is null)
        {
            return null;
        }

        try
        {
            List<SensorCandidate> candidates = new();
            foreach (IHardware hardware in _computer.Hardware)
            {
                CollectTemperatureSensors(hardware, candidates);
            }

            SensorCandidate? selected = SelectBestCandidate(candidates);
            if (selected is null)
            {
                return null;
            }

            string pawnIoDiagnostic = GetPawnIoDiagnostic();
            return Available(
                selected.Value,
                selected.SensorName,
                IsPawnIoInstalled() ? "LibreHardwareMonitor + PawnIO" : "LibreHardwareMonitor",
                true,
                $"Using CPU sensor: {selected.SensorName}. {pawnIoDiagnostic}");
        }
        catch (Exception exception)
        {
            _openError = exception.Message;
            return null;
        }
    }

    private CpuPackagePowerReading? TryReadLibreHardwareMonitorCpuPackagePower()
    {
        if (_computer is null)
        {
            return null;
        }

        try
        {
            List<PowerCandidate> candidates = new();
            foreach (IHardware hardware in _computer.Hardware)
            {
                CollectCpuPackagePowerSensors(hardware, candidates);
            }

            PowerCandidate? selected = candidates
                .OrderByDescending(candidate => candidate.Priority)
                .ThenByDescending(candidate => candidate.Watts)
                .FirstOrDefault();

            if (selected is null)
            {
                return null;
            }

            string source = IsPawnIoInstalled()
                ? "LibreHardwareMonitor + PawnIO"
                : "LibreHardwareMonitor";
            return new CpuPackagePowerReading(
                true,
                selected.Watts,
                $"{selected.Watts:0.0} W",
                selected.SensorName,
                source,
                $"Using CPU package power sensor: {selected.SensorName}.");
        }
        catch
        {
            return null;
        }
    }

    private CpuClockReading? TryReadLibreHardwareMonitorCpuClock()
    {
        if (_computer is null)
        {
            return null;
        }

        try
        {
            List<ClockCandidate> candidates = new();
            foreach (IHardware hardware in _computer.Hardware)
            {
                CollectCpuClockSensors(hardware, candidates);
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            int bestPriority = candidates.Max(item => item.Priority);
            List<ClockCandidate> selected = candidates
                .Where(candidate => candidate.Priority == bestPriority)
                .ToList();

            double averageMegahertz = selected.Average(candidate => candidate.Megahertz);
            string source = IsPawnIoInstalled()
                ? "LibreHardwareMonitor + PawnIO"
                : "LibreHardwareMonitor";
            return new CpuClockReading(
                true,
                averageMegahertz,
                FormatCpuClock(averageMegahertz),
                selected.Count == 1 ? selected[0].SensorName : $"Average of {selected.Count} CPU core clocks",
                source,
                $"Using {selected.Count} live CPU clock sensor(s); bus/reference clocks are excluded.");
        }
        catch
        {
            return null;
        }
    }

    private GpuPowerReading? TryReadLibreHardwareMonitorGpuPower()
    {
        if (_computer is null)
        {
            return null;
        }

        try
        {
            List<PowerCandidate> candidates = new();
            foreach (IHardware hardware in _computer.Hardware)
            {
                CollectGpuPowerSensors(hardware, candidates);
            }

            PowerCandidate? selected = candidates
                .OrderByDescending(candidate => candidate.Priority)
                .ThenByDescending(candidate => candidate.Watts)
                .FirstOrDefault();

            if (selected is null)
            {
                return null;
            }

            string source = IsPawnIoInstalled()
                ? "LibreHardwareMonitor + PawnIO"
                : "LibreHardwareMonitor";
            return new GpuPowerReading(
                true,
                selected.Watts,
                $"{selected.Watts:0.0} W",
                selected.SensorName,
                source,
                $"Using GPU power sensor: {selected.SensorName}.");
        }
        catch
        {
            return null;
        }
    }

    private GpuTemperatureReading? TryReadLibreHardwareMonitorGpuTemperature()
    {
        if (_computer is null)
        {
            return null;
        }

        try
        {
            List<SensorCandidate> candidates = new();
            foreach (IHardware hardware in _computer.Hardware)
            {
                CollectGpuTemperatureSensors(hardware, candidates);
            }

            SensorCandidate? selected = candidates
                .OrderByDescending(candidate => candidate.Priority)
                .ThenByDescending(candidate => candidate.Value)
                .FirstOrDefault();

            if (selected is null)
            {
                return null;
            }

            string source = IsPawnIoInstalled()
                ? "LibreHardwareMonitor + PawnIO"
                : "LibreHardwareMonitor";
            return new GpuTemperatureReading(
                true,
                selected.Value,
                $"{selected.Value:0.0} °C",
                selected.SensorName,
                source,
                $"Using GPU temperature sensor: {selected.SensorName}.");
        }
        catch
        {
            return null;
        }
    }

    private CpuTemperatureReading? TryReadHwiNfoSharedMemory()
    {
        try
        {
            using SharedMemoryReader reader = new(mutexTimeout: 300);
            SensorReading[] readings = reader.ReadLocal().ToArray();

            if (readings.Length == 0)
            {
                _hwiNfoDiagnostic =
                    "HWiNFO shared memory is active but returned no sensor readings. Keep the HWiNFO Sensors window running or minimized.";
                return null;
            }

            List<SensorCandidate> candidates = new();
            foreach (SensorReading reading in readings)
            {
                if (reading.Type != HwiNfoSensorType.SensorTypeTemp ||
                    reading.Value is < 0 or > 125)
                {
                    continue;
                }

                string groupName = FirstNonEmpty(
                    reading.GroupLabelUser,
                    reading.GroupLabelOrig,
                    "HWiNFO sensor group");

                string labelName = FirstNonEmpty(
                    reading.LabelUser,
                    reading.LabelOrig,
                    "Temperature");

                int priority = GetHwiNfoSensorPriority(groupName, labelName);
                if (priority <= 0)
                {
                    continue;
                }

                candidates.Add(new SensorCandidate(
                    reading.Value,
                    $"{groupName} - {labelName}",
                    priority));
            }

            SensorCandidate? selected = SelectBestCandidate(candidates);
            if (selected is null)
            {
                _hwiNfoDiagnostic =
                    $"HWiNFO shared memory is available and exposed {readings.Length} readings, but no CPU temperature reading was identified.";
                return null;
            }

            _hwiNfoDiagnostic =
                $"Using HWiNFO shared-memory CPU sensor: {selected.SensorName}";

            return Available(
                selected.Value,
                selected.SensorName,
                "HWiNFO shared memory",
                true,
                _hwiNfoDiagnostic);
        }
        catch (FileNotFoundException)
        {
            _hwiNfoDiagnostic =
                "HWiNFO shared memory is not active. Run HWiNFO in Sensors-only mode, enable Shared Memory Support in HWiNFO settings, keep its Sensors window open or minimized, then select Rescan sensors.";
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            _hwiNfoDiagnostic =
                $"Windows blocked access to HWiNFO shared memory: {exception.Message} Try running both applications with the same privilege level.";
            return null;
        }
        catch (Exception exception)
        {
            _hwiNfoDiagnostic =
                $"HWiNFO shared-memory reading failed: {exception.Message}";
            return null;
        }
    }

    private static int GetHwiNfoSensorPriority(string groupName, string labelName)
    {
        string combinedName = $"{groupName} {labelName}";

        if (ContainsAny(
                combinedName,
                "gpu",
                "graphics",
                "hot spot",
                "memory junction",
                "gddr",
                "ssd",
                "nvme",
                "drive",
                "dimm",
                "pch",
                "vrm",
                "motherboard"))
        {
            return 0;
        }

        if (ContainsAny(
                labelName,
                "distance to tjmax",
                "thermal throttling",
                "critical temperature",
                "temperature limit"))
        {
            return 0;
        }

        bool cpuGroup = ContainsAny(
            groupName,
            "cpu",
            "processor",
            "amd ryzen",
            "intel core",
            "core ultra",
            "amd64");

        bool cpuLabel = ContainsAny(
            labelName,
            "cpu",
            "processor",
            "package",
            "tctl",
            "tdie",
            "cpu die",
            "core",
            "ccd",
            "soc");

        if (!cpuGroup && !cpuLabel)
        {
            return 0;
        }

        if (ContainsAny(labelName, "cpu package", "cpu (tctl/tdie)", "tctl/tdie"))
        {
            return 150;
        }

        if (ContainsAny(labelName, "cpu die (average)", "cpu die average"))
        {
            return 145;
        }

        if (ContainsAny(labelName, "cpu die", "tctl", "tdie"))
        {
            return 140;
        }

        if (ContainsAny(labelName, "core max", "cpu max"))
        {
            return 135;
        }

        if (ContainsAny(labelName, "package"))
        {
            return 130;
        }

        if (cpuGroup && ContainsAny(labelName, "cpu", "average", "core temperatures"))
        {
            return 120;
        }

        if (cpuGroup && ContainsAny(labelName, "core", "ccd", "soc"))
        {
            return 110;
        }

        return cpuGroup ? 100 : 80;
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value)).Trim();

    private static void CollectTemperatureSensors(
        IHardware hardware,
        ICollection<SensorCandidate> candidates)
    {
        try
        {
            hardware.Update();
        }
        catch
        {
            // Continue scanning other hardware when one device cannot be updated.
        }

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType != LibreSensorType.Temperature || !sensor.Value.HasValue)
            {
                continue;
            }

            float value = sensor.Value.Value;
            if (value is < 0 or > 125)
            {
                continue;
            }

            int priority = GetSensorPriority(hardware, sensor);
            if (priority <= 0)
            {
                continue;
            }

            candidates.Add(new SensorCandidate(
                value,
                $"{hardware.Name} - {sensor.Name}",
                priority));
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            CollectTemperatureSensors(subHardware, candidates);
        }
    }

    private static int GetSensorPriority(IHardware hardware, ISensor sensor)
    {
        string sensorName = sensor.Name;
        string hardwareName = hardware.Name;

        bool packageSensor = ContainsAny(
            sensorName,
            "package",
            "tctl",
            "tdie",
            "cpu die",
            "core max",
            "cpu max");

        bool cpuNamedSensor = ContainsAny(
            sensorName,
            "cpu",
            "processor",
            "core",
            "ccd",
            "soc");

        bool cpuNamedHardware = ContainsAny(
            hardwareName,
            "cpu",
            "processor",
            "amd ryzen",
            "amd64");

        if (hardware.HardwareType == HardwareType.Cpu && packageSensor)
        {
            return 100;
        }

        if (hardware.HardwareType == HardwareType.Cpu && cpuNamedSensor)
        {
            return 90;
        }

        if (hardware.HardwareType == HardwareType.Cpu)
        {
            return 80;
        }

        if (packageSensor && (cpuNamedHardware || cpuNamedSensor))
        {
            return 70;
        }

        return cpuNamedSensor || cpuNamedHardware ? 60 : 0;
    }

    private static void CollectCpuPackagePowerSensors(
        IHardware hardware,
        ICollection<PowerCandidate> candidates)
    {
        try
        {
            hardware.Update();
        }
        catch
        {
            // Continue scanning the rest of the hardware tree.
        }

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType != LibreSensorType.Power || !sensor.Value.HasValue)
            {
                continue;
            }

            double watts = sensor.Value.Value;
            if (watts is < 0 or > 2000)
            {
                continue;
            }

            string sensorName = sensor.Name;
            string hardwareName = hardware.Name;
            string combined = $"{hardwareName} {sensorName}";
            bool cpuHardware = hardware.HardwareType == HardwareType.Cpu ||
                ContainsAny(hardwareName, "cpu", "processor", "intel core", "amd ryzen");

            int priority = 0;
            if (cpuHardware && ContainsAny(sensorName, "cpu package", "package power"))
            {
                priority = 140;
            }
            else if (cpuHardware && ContainsAny(sensorName, "package"))
            {
                priority = 130;
            }
            else if (cpuHardware && ContainsAny(sensorName, "cpu total", "total cpu", "processor power", "cpu ppt", "total power"))
            {
                priority = 120;
            }
            else if (ContainsAny(combined, "cpu package", "package power"))
            {
                priority = 110;
            }

            if (priority > 0)
            {
                candidates.Add(new PowerCandidate(
                    watts,
                    $"{hardwareName} - {sensorName}",
                    priority));
            }
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            CollectCpuPackagePowerSensors(subHardware, candidates);
        }
    }

    private static void CollectCpuClockSensors(
        IHardware hardware,
        ICollection<ClockCandidate> candidates)
    {
        try
        {
            hardware.Update();
        }
        catch
        {
            // Continue scanning the rest of the hardware tree.
        }

        bool cpuHardware = hardware.HardwareType == HardwareType.Cpu ||
            ContainsAny(hardware.Name, "cpu", "processor", "intel core", "amd ryzen");
        if (cpuHardware)
        {
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.SensorType != LibreSensorType.Clock || !sensor.Value.HasValue)
                {
                    continue;
                }

                double megahertz = sensor.Value.Value;
                string name = sensor.Name;
                if (!double.IsFinite(megahertz) || megahertz is < 100 or > 15000 ||
                    ContainsAny(name, "bus", "reference", "memory", "uncore", "fabric"))
                {
                    continue;
                }

                int priority = ContainsAny(name, "effective clock")
                    ? 140
                    : ContainsAny(name, "cpu core", "core #", "core clock")
                        ? 130
                        : ContainsAny(name, "core")
                            ? 120
                            : 100;
                candidates.Add(new ClockCandidate(
                    megahertz,
                    $"{hardware.Name} - {name}",
                    priority));
            }
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            CollectCpuClockSensors(subHardware, candidates);
        }
    }

    private static CpuClockReading? TryReadWindowsCpuClock()
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                "SELECT Name, CurrentClockSpeed FROM Win32_Processor");
            List<double> clocks = new();
            List<string> names = new();
            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                if (double.TryParse(
                        Convert.ToString(item["CurrentClockSpeed"], CultureInfo.InvariantCulture),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double megahertz) &&
                    double.IsFinite(megahertz) &&
                    megahertz is >= 100 and <= 15000)
                {
                    clocks.Add(megahertz);
                    names.Add(Convert.ToString(item["Name"], CultureInfo.InvariantCulture)?.Trim() ?? "Windows processor");
                }
            }

            if (clocks.Count == 0)
            {
                return null;
            }

            double averageMegahertz = clocks.Average();
            return new CpuClockReading(
                true,
                averageMegahertz,
                FormatCpuClock(averageMegahertz),
                names.Count == 1 ? names[0] : $"Average of {names.Count} processors",
                "Windows WMI",
                "Using Win32_Processor CurrentClockSpeed because live per-core clock sensors were not exposed. Some firmware reports base speed here rather than instantaneous turbo speed.");
        }
        catch
        {
            return null;
        }
    }

    private static void CollectGpuPowerSensors(
        IHardware hardware,
        ICollection<PowerCandidate> candidates)
    {
        try
        {
            hardware.Update();
        }
        catch
        {
            // Continue scanning the rest of the hardware tree.
        }

        bool gpuHardware = hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel ||
            ContainsAny(hardware.Name, "gpu", "graphics", "geforce", "radeon", "arc");

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (!gpuHardware || sensor.SensorType != LibreSensorType.Power || !sensor.Value.HasValue)
            {
                continue;
            }

            double watts = sensor.Value.Value;
            if (watts is < 0 or > 2000)
            {
                continue;
            }

            string name = sensor.Name;
            int priority = ContainsAny(name, "gpu package", "board power", "total graphics power", "gpu power", "total power")
                ? 150
                : ContainsAny(name, "package", "ppt", "asic power")
                    ? 130
                    : 100;

            candidates.Add(new PowerCandidate(
                watts,
                $"{hardware.Name} - {sensor.Name}",
                priority));
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            CollectGpuPowerSensors(subHardware, candidates);
        }
    }

    private static void CollectGpuTemperatureSensors(
        IHardware hardware,
        ICollection<SensorCandidate> candidates)
    {
        try
        {
            hardware.Update();
        }
        catch
        {
            // Continue scanning the rest of the hardware tree.
        }

        bool gpuHardware = hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel ||
            ContainsAny(hardware.Name, "gpu", "graphics", "geforce", "radeon", "arc");

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (!gpuHardware || sensor.SensorType != LibreSensorType.Temperature || !sensor.Value.HasValue)
            {
                continue;
            }

            double celsius = sensor.Value.Value;
            if (!double.IsFinite(celsius) || celsius is < 0 or > 130)
            {
                continue;
            }

            string name = sensor.Name;
            int priority = ContainsAny(name, "hot spot", "hotspot", "junction")
                ? 170
                : ContainsAny(name, "gpu core", "core temperature", "gpu temperature")
                    ? 160
                    : ContainsAny(name, "memory", "vram")
                        ? 150
                        : 120;

            candidates.Add(new SensorCandidate(
                celsius,
                $"{hardware.Name} - {sensor.Name}",
                priority));
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            CollectGpuTemperatureSensors(subHardware, candidates);
        }
    }

    private static GpuTemperatureReading? TryReadGpuTemperatureFromHardwareMonitorWmiProvider(
        string namespacePath,
        string sourceName)
    {
        try
        {
            ManagementScope scope = new(namespacePath);
            scope.Connect();

            ObjectQuery query = new(
                "SELECT Name, Identifier, Value FROM Sensor WHERE SensorType = 'Temperature'");

            using ManagementObjectSearcher searcher = new(scope, query);
            using ManagementObjectCollection results = searcher.Get();

            List<SensorCandidate> candidates = new();
            foreach (ManagementBaseObject sensor in results)
            {
                string name = Convert.ToString(sensor["Name"], CultureInfo.InvariantCulture) ?? "GPU temperature";
                string identifier = Convert.ToString(sensor["Identifier"], CultureInfo.InvariantCulture) ?? string.Empty;
                if (!ContainsAny(identifier, "gpu", "nvidiagpu", "amdgpu", "intelgpu"))
                {
                    continue;
                }

                if (!double.TryParse(
                        Convert.ToString(sensor["Value"], CultureInfo.InvariantCulture),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double celsius) ||
                    !double.IsFinite(celsius) ||
                    celsius is < 0 or > 130)
                {
                    continue;
                }

                int priority = ContainsAny(name, "hot spot", "hotspot", "junction")
                    ? 160
                    : ContainsAny(name, "gpu core", "core temperature", "gpu temperature")
                        ? 150
                        : ContainsAny(name, "memory", "vram")
                            ? 140
                            : 110;
                candidates.Add(new SensorCandidate(celsius, name, priority));
            }

            SensorCandidate? selected = candidates
                .OrderByDescending(candidate => candidate.Priority)
                .ThenByDescending(candidate => candidate.Value)
                .FirstOrDefault();

            return selected is null
                ? null
                : new GpuTemperatureReading(
                    true,
                    selected.Value,
                    $"{selected.Value:0.0} °C",
                    selected.SensorName,
                    sourceName,
                    $"Using GPU temperature sensor from {sourceName}: {selected.SensorName}.");
        }
        catch
        {
            return null;
        }
    }

    private static GpuPowerReading? TryReadGpuPowerFromHardwareMonitorWmiProvider(
        string namespacePath,
        string sourceName)
    {
        try
        {
            ManagementScope scope = new(namespacePath);
            scope.Connect();

            ObjectQuery query = new(
                "SELECT Name, Identifier, Value FROM Sensor WHERE SensorType = 'Power'");

            using ManagementObjectSearcher searcher = new(scope, query);
            using ManagementObjectCollection results = searcher.Get();

            List<PowerCandidate> candidates = new();
            foreach (ManagementBaseObject sensor in results)
            {
                string name = Convert.ToString(sensor["Name"], CultureInfo.InvariantCulture) ?? "Power sensor";
                string identifier = Convert.ToString(sensor["Identifier"], CultureInfo.InvariantCulture) ?? string.Empty;
                bool gpuIdentifier = ContainsAny(identifier, "gpu", "nvidiagpu", "amdgpu", "intelgpu");
                if (!gpuIdentifier)
                {
                    continue;
                }

                if (!double.TryParse(
                        Convert.ToString(sensor["Value"], CultureInfo.InvariantCulture),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double watts) ||
                    watts is < 0 or > 2000)
                {
                    continue;
                }

                int priority = ContainsAny(name, "gpu package", "board power", "total graphics power", "gpu power", "total power")
                    ? 140
                    : 100;
                candidates.Add(new PowerCandidate(watts, name, priority));
            }

            PowerCandidate? selected = candidates
                .OrderByDescending(candidate => candidate.Priority)
                .ThenByDescending(candidate => candidate.Watts)
                .FirstOrDefault();

            return selected is null
                ? null
                : new GpuPowerReading(
                    true,
                    selected.Watts,
                    $"{selected.Watts:0.0} W",
                    selected.SensorName,
                    sourceName,
                    $"Using GPU power sensor from {sourceName}: {selected.SensorName}.");
        }
        catch
        {
            return null;
        }
    }

    private static CpuPackagePowerReading? TryReadPowerFromHardwareMonitorWmiProvider(
        string namespacePath,
        string sourceName)
    {
        try
        {
            ManagementScope scope = new(namespacePath);
            scope.Connect();

            ObjectQuery query = new(
                "SELECT Name, Identifier, Value FROM Sensor WHERE SensorType = 'Power'");

            using ManagementObjectSearcher searcher = new(scope, query);
            using ManagementObjectCollection results = searcher.Get();

            List<PowerCandidate> candidates = new();
            foreach (ManagementBaseObject sensor in results)
            {
                string name = Convert.ToString(sensor["Name"], CultureInfo.InvariantCulture) ?? "Power sensor";
                string identifier = Convert.ToString(sensor["Identifier"], CultureInfo.InvariantCulture) ?? string.Empty;
                bool cpuIdentifier = ContainsAny(identifier, "cpu/", "intelcpu", "amdcpu");
                bool packageNamed = ContainsAny(
                    name,
                    "cpu package",
                    "package power",
                    "package",
                    "cpu ppt",
                    "total power",
                    "processor power");

                if (!cpuIdentifier || !packageNamed)
                {
                    continue;
                }

                if (!double.TryParse(
                        Convert.ToString(sensor["Value"], CultureInfo.InvariantCulture),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double watts) ||
                    watts is < 0 or > 2000)
                {
                    continue;
                }

                int priority = ContainsAny(name, "cpu package", "package power", "package") ? 120 : 100;
                candidates.Add(new PowerCandidate(watts, name, priority));
            }

            PowerCandidate? selected = candidates
                .OrderByDescending(candidate => candidate.Priority)
                .ThenByDescending(candidate => candidate.Watts)
                .FirstOrDefault();

            return selected is null
                ? null
                : new CpuPackagePowerReading(
                    true,
                    selected.Watts,
                    $"{selected.Watts:0.0} W",
                    selected.SensorName,
                    sourceName,
                    $"Using CPU package power sensor from {sourceName}: {selected.SensorName}.");
        }
        catch
        {
            return null;
        }
    }

    private static void CollectFanSensors(
        IHardware hardware,
        ICollection<FanCandidate> candidates,
        ICollection<FanControlCandidate> controls)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (!sensor.Value.HasValue)
            {
                continue;
            }

            string combined = $"{hardware.Name} {sensor.Name}";
            int priority = ContainsAny(combined, "cpu", "processor")
                ? 100
                : ContainsAny(combined, "system", "chassis", "fan", "blower")
                    ? 70
                    : ContainsAny(combined, "gpu", "graphics")
                        ? 50
                        : 40;

            if (sensor.SensorType == LibreSensorType.Fan)
            {
                double rpm = sensor.Value.Value;
                if (rpm is >= 0 and <= 30000)
                {
                    candidates.Add(new FanCandidate(
                        rpm,
                        $"{hardware.Name} - {sensor.Name}",
                        priority));
                }
            }
            else if (sensor.SensorType == LibreSensorType.Control)
            {
                double percent = sensor.Value.Value;
                if (percent is >= 0 and <= 100 && ContainsAny(combined, "fan", "blower", "cool"))
                {
                    controls.Add(new FanControlCandidate(
                        percent,
                        $"{hardware.Name} - {sensor.Name}",
                        priority));
                }
            }
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            CollectFanSensors(subHardware, candidates, controls);
        }
    }

    private static void AppendFanDiagnostics(
        IHardware hardware,
        StringBuilder report,
        int depth,
        ref int fanCount,
        ref int controlCount)
    {
        string indent = new(' ', depth * 2);
        report.AppendLine($"{indent}{hardware.HardwareType}: {hardware.Name}");
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType != LibreSensorType.Fan && sensor.SensorType != LibreSensorType.Control)
            {
                continue;
            }

            if (sensor.SensorType == LibreSensorType.Fan)
            {
                fanCount++;
            }
            else
            {
                controlCount++;
            }

            string value = sensor.Value.HasValue
                ? sensor.SensorType == LibreSensorType.Fan
                    ? $"{sensor.Value.Value:0} RPM"
                    : $"{sensor.Value.Value:0.0}%"
                : "no value";
            report.AppendLine($"{indent}  {sensor.SensorType}: {sensor.Name} = {value} [{sensor.Identifier}]");
        }

        foreach (IHardware subHardware in hardware.SubHardware)
        {
            AppendFanDiagnostics(subHardware, report, depth + 1, ref fanCount, ref controlCount);
        }
    }

    private static SensorCandidate? SelectBestCandidate(IEnumerable<SensorCandidate> candidates)
    {
        return candidates
            .OrderByDescending(candidate => candidate.Priority)
            .ThenByDescending(candidate => candidate.Value)
            .FirstOrDefault();
    }

    private static CpuTemperatureReading? TryReadHardwareMonitorWmiProvider(
        string namespacePath,
        string sourceName)
    {
        try
        {
            ManagementScope scope = new(namespacePath);
            scope.Connect();

            ObjectQuery query = new(
                "SELECT Name, Identifier, Value FROM Sensor WHERE SensorType = 'Temperature'");

            using ManagementObjectSearcher searcher = new(scope, query);
            using ManagementObjectCollection results = searcher.Get();

            List<SensorCandidate> candidates = new();
            foreach (ManagementBaseObject sensor in results)
            {
                string name = Convert.ToString(sensor["Name"], CultureInfo.InvariantCulture) ?? "Temperature sensor";
                string identifier = Convert.ToString(sensor["Identifier"], CultureInfo.InvariantCulture) ?? string.Empty;
                string combinedName = $"{name} {identifier}";

                if (!ContainsAny(
                        combinedName,
                        "cpu",
                        "intelcpu",
                        "amdcpu",
                        "package",
                        "tctl",
                        "tdie",
                        "core",
                        "ccd"))
                {
                    continue;
                }

                if (!TryConvertTemperature(sensor["Value"], out double value))
                {
                    continue;
                }

                int priority = ContainsAny(name, "package", "tctl", "tdie", "core max") ? 100 : 80;
                candidates.Add(new SensorCandidate(value, name, priority));
            }

            SensorCandidate? selected = SelectBestCandidate(candidates);
            return selected is null
                ? null
                : Available(
                    selected.Value,
                    selected.SensorName,
                    sourceName,
                    true,
                    $"Using CPU sensor from {sourceName}: {selected.SensorName}");
        }
        catch
        {
            return null;
        }
    }

    private static CpuFanReading? TryReadFanFromHardwareMonitorWmiProvider(
        string namespacePath,
        string sourceName)
    {
        try
        {
            ManagementScope scope = new(namespacePath);
            scope.Connect();

            ObjectQuery query = new(
                "SELECT Name, Identifier, Value FROM Sensor WHERE SensorType = 'Fan'");

            using ManagementObjectSearcher searcher = new(scope, query);
            using ManagementObjectCollection results = searcher.Get();

            List<FanCandidate> candidates = new();
            foreach (ManagementBaseObject sensor in results)
            {
                string name = Convert.ToString(sensor["Name"], CultureInfo.InvariantCulture) ?? "Fan";
                string identifier = Convert.ToString(sensor["Identifier"], CultureInfo.InvariantCulture) ?? string.Empty;
                if (!double.TryParse(
                        Convert.ToString(sensor["Value"], CultureInfo.InvariantCulture),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double rpm) ||
                    rpm is < 0 or > 30000)
                {
                    continue;
                }

                string combined = $"{name} {identifier}";
                int priority = ContainsAny(combined, "cpu", "processor") ? 100 : 60;
                candidates.Add(new FanCandidate(rpm, name, priority));
            }

            FanCandidate? selected = candidates
                .OrderByDescending(candidate => candidate.Priority)
                .ThenByDescending(candidate => candidate.Rpm)
                .FirstOrDefault();

            return selected is null
                ? null
                : new CpuFanReading(
                    true,
                    selected.Rpm,
                    $"Fan: {selected.Rpm:0} RPM",
                    selected.SensorName,
                    sourceName,
                    $"Fan RPM detected through {sourceName}.");
        }
        catch
        {
            return null;
        }
    }

    private static CpuTemperatureReading? TryReadAcpiThermalZoneDiagnostic()
    {
        try
        {
            ManagementScope scope = new(@"\\.\root\WMI");
            scope.Connect();

            ObjectQuery query = new(
                "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature");

            using ManagementObjectSearcher searcher = new(scope, query);
            using ManagementObjectCollection results = searcher.Get();

            List<AcpiCandidate> candidates = new();
            foreach (ManagementBaseObject zone in results)
            {
                if (zone["CurrentTemperature"] is null)
                {
                    continue;
                }

                double rawTemperature = Convert.ToDouble(
                    zone["CurrentTemperature"],
                    CultureInfo.InvariantCulture);

                double celsius = (rawTemperature / 10.0) - 273.15;
                if (celsius is < 0 or > 125)
                {
                    continue;
                }

                string instanceName = Convert.ToString(
                    zone["InstanceName"],
                    CultureInfo.InvariantCulture) ?? "ACPI thermal zone";

                bool cpuSpecific = ContainsAny(
                    instanceName,
                    "cpu",
                    "processor",
                    "pkg");

                candidates.Add(new AcpiCandidate(celsius, instanceName, cpuSpecific));
            }

            AcpiCandidate? selected = candidates
                .OrderByDescending(candidate => candidate.IsCpuSpecific)
                .ThenByDescending(candidate => candidate.Value)
                .FirstOrDefault();

            if (selected is null)
            {
                return null;
            }

            // MSAcpi_ThermalZoneTemperature is frequently a chassis, motherboard,
            // firmware, or ambient zone rather than the processor package. Some
            // systems also expose a fixed value (for example 25.1 C) for long
            // periods. Do not label this as CPU temperature or use it for alerts.
            string diagnostic =
                $"Windows ACPI reports thermal zone {selected.SensorName} at {selected.Value:0.0} C, " +
                "but ACPI thermal zones are not considered reliable CPU package sensors. " +
                "This value is shown only as a diagnostic and is excluded from CPU temperature display and alerts.";

            return Unavailable(diagnostic, "Windows ACPI thermal-zone diagnostic");
        }
        catch
        {
            return null;
        }
    }

    private string BuildUnavailableDiagnostic()
    {
        string pawnIoDiagnostic = GetPawnIoDiagnostic();
        string baseMessage =
            "No CPU-specific temperature sensor was exposed by the built-in LibreHardwareMonitor engine. " +
            "Windows Utility by Sajith does not require HWiNFO. The installer includes the signed PawnIO low-level hardware driver used by current LibreHardwareMonitor builds. " +
            "Generic Windows ACPI thermal zones are diagnostic-only because they may not represent CPU package temperature. Use Rescan sensors after installing/upgrading PawnIO or after a Windows restart.";

        string diagnostic = $"{pawnIoDiagnostic} {baseMessage} Optional HWiNFO fallback status: {_hwiNfoDiagnostic}";

        return string.IsNullOrWhiteSpace(_openError)
            ? diagnostic
            : $"{diagnostic} Direct sensor access error: {_openError}";
    }

    private static bool IsPawnIoInstalled()
    {
        return TryGetPawnIoVersion(out _);
    }

    private static string GetPawnIoDiagnostic()
    {
        return TryGetPawnIoVersion(out string version)
            ? $"PawnIO {version} is installed for low-level hardware access."
            : "PawnIO is not detected. Re-run the Windows Utility by Sajith installer as administrator so it can install the hardware sensor driver.";
    }

    private static bool TryGetPawnIoVersion(out string version)
    {
        const string registryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? key = baseKey.OpenSubKey(registryPath);
                string? value = key?.GetValue("DisplayVersion") as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    version = value.Trim();
                    return true;
                }
            }
            catch
            {
            }
        }

        version = string.Empty;
        return false;
    }

    private static bool TryConvertTemperature(object? valueObject, out double value)
    {
        value = 0;
        if (valueObject is null)
        {
            return false;
        }

        try
        {
            value = Convert.ToDouble(valueObject, CultureInfo.InvariantCulture);
            return value is >= 0 and <= 125;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static bool IsSoftwareDisplayAdapter(string name) => ContainsAny(
        name,
        "microsoft basic display",
        "remote display",
        "remote desktop",
        "indirect display",
        "virtual display");

    private static GraphicsAdapterKind ClassifyGraphicsAdapter(string name)
    {
        if (ContainsAny(name, "nvidia", "geforce", "quadro", "tesla", "rtx", "gtx", "intel arc") ||
            ContainsAny(name, "radeon rx", "radeon pro", "firepro"))
        {
            return GraphicsAdapterKind.Dedicated;
        }

        if ((ContainsAny(name, "intel") && ContainsAny(name, "graphics", "iris", "uhd", "hd graphics")) ||
            ContainsAny(name, "amd radeon(tm) graphics", "radeon vega graphics"))
        {
            return GraphicsAdapterKind.Integrated;
        }

        return GraphicsAdapterKind.Unknown;
    }

    private static string FormatCpuClock(double megahertz) => megahertz >= 1000
        ? $"{megahertz / 1000.0:0.00} GHz"
        : $"{megahertz:0} MHz";

    private static CpuTemperatureReading Available(
        double value,
        string sensorName,
        string source,
        bool isCpuSpecific,
        string diagnostic) => new(
            true,
            value,
            $"{value:0.0} °C",
            sensorName,
            diagnostic,
            source,
            isCpuSpecific);

    private static CpuTemperatureReading Unavailable(string diagnostic, string source = "None") => new(
        false,
        null,
        "Not available",
        string.Empty,
        diagnostic,
        source,
        false);

    private static CpuPackagePowerReading PowerUnavailable(string diagnostic) => new(
        false,
        null,
        "Not available",
        string.Empty,
        "LibreHardwareMonitor",
        diagnostic);

    private static CpuClockReading ClockUnavailable(string diagnostic) => new(
        false,
        null,
        "Not available",
        string.Empty,
        "LibreHardwareMonitor / Windows WMI",
        diagnostic);

    private static GpuPowerReading GpuPowerUnavailable(string diagnostic) => new(
        false,
        null,
        "Not available",
        string.Empty,
        "LibreHardwareMonitor",
        diagnostic);

    private static GpuTemperatureReading GpuTemperatureUnavailable(string diagnostic) => new(
        false,
        null,
        "Not available",
        string.Empty,
        "LibreHardwareMonitor",
        diagnostic);

    private static CpuFanReading FanUnavailable(string diagnostic = "Fan RPM was not reported by the hardware.") => new(
        false,
        null,
        "Fan: RPM unavailable",
        string.Empty,
        "LibreHardwareMonitor",
        diagnostic);

    private void CloseComputer()
    {
        if (_computer is null)
        {
            return;
        }

        try
        {
            _computer.Close();
        }
        catch
        {
            // Closing sensor access should not prevent rescanning or shutdown.
        }
        finally
        {
            _computer = null;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            CloseComputer();
            _disposed = true;
        }
    }

    private sealed record SensorCandidate(double Value, string SensorName, int Priority);

    private sealed record PowerCandidate(double Watts, string SensorName, int Priority);

    private sealed record ClockCandidate(double Megahertz, string SensorName, int Priority);

    private sealed record FanCandidate(double Rpm, string SensorName, int Priority);

    private sealed record FanControlCandidate(double Percent, string SensorName, int Priority);

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }

    private sealed record AcpiCandidate(double Value, string SensorName, bool IsCpuSpecific);

    private enum GraphicsAdapterKind
    {
        Integrated,
        Dedicated,
        Unknown
    }
}
