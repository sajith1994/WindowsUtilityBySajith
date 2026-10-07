using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public enum HealthStatus
{
    Healthy,
    Attention,
    ActionNeeded,
    Unknown
}

/// <summary>
/// One assessed area of the machine, in the shape the dashboard presents it: a verdict first,
/// the supporting number second.
/// </summary>
public sealed record HealthDomain(
    string Name,
    HealthStatus Status,
    string Headline,
    string Reason);

/// <summary>
/// Turns raw sensor and inventory data into verdicts.
/// </summary>
/// <remarks>
/// The dashboard previously showed only measurements: "63.0 °C", "0% used", "Connected". Those
/// are correct but they put the interpretation on whoever is reading the screen. This service
/// holds the thresholds in one place so the UI can lead with a verdict and keep the numbers as
/// evidence, and so the same rule is not reimplemented differently on each card.
///
/// Thresholds are deliberately conservative: a technician tool that cries wolf gets ignored.
/// </remarks>
public sealed class PcHealthService
{
    // Sustained temperatures above this on a desktop part usually mean a cooling problem worth
    // investigating; throttling generally begins near 100 °C.
    private const double CpuTemperatureAttentionCelsius = 85;
    private const double CpuTemperatureActionCelsius = 95;

    private const double GpuTemperatureAttentionCelsius = 83;
    private const double GpuTemperatureActionCelsius = 90;

    // Below 10% free, Windows update and page-file behaviour start to suffer.
    private const double DiskFreeAttentionPercent = 15;
    private const double DiskFreeActionPercent = 10;

    public HealthDomain AssessCooling(
        CpuTemperatureReading cpuTemperature,
        GpuTemperatureReading gpuTemperature,
        FanInventoryReading fans)
    {
        if (!cpuTemperature.IsAvailable && !gpuTemperature.IsAvailable)
        {
            return new HealthDomain("Cooling", HealthStatus.Unknown, "Sensors unavailable",
                "No temperature sensor could be read. Check that the background service is running.");
        }

        double? cpu = cpuTemperature.IsAvailable ? cpuTemperature.ValueCelsius : null;
        double? gpu = gpuTemperature.IsAvailable ? gpuTemperature.ValueCelsius : null;

        // A fan reporting zero while the part is hot is the clearest cooling fault there is.
        bool stalledFan = fans.IsAvailable &&
            fans.Fans.Any(fan => fan.Rpm <= 0) &&
            ((cpu ?? 0) > 70 || (gpu ?? 0) > 70);

        if (stalledFan)
        {
            return new HealthDomain("Cooling", HealthStatus.ActionNeeded, "Fan not spinning",
                "A fan reports 0 RPM while the system is warm. Check the header connection and the fan itself.");
        }

        double hottest = Math.Max(cpu ?? 0, gpu ?? 0);
        string detail = FormatTemperatures(cpu, gpu);

        if ((cpu ?? 0) >= CpuTemperatureActionCelsius || (gpu ?? 0) >= GpuTemperatureActionCelsius)
        {
            return new HealthDomain("Cooling", HealthStatus.ActionNeeded, $"Running hot - {hottest:0} °C",
                $"{detail} Clean the heatsinks and check thermal paste and case airflow.");
        }

        if ((cpu ?? 0) >= CpuTemperatureAttentionCelsius || (gpu ?? 0) >= GpuTemperatureAttentionCelsius)
        {
            return new HealthDomain("Cooling", HealthStatus.Attention, $"Warm - {hottest:0} °C",
                $"{detail} Fine under load, worth watching if it stays here at idle.");
        }

        return new HealthDomain("Cooling", HealthStatus.Healthy, $"Normal - {hottest:0} °C", detail);
    }

    private static string FormatTemperatures(double? cpu, double? gpu)
    {
        if (cpu.HasValue && gpu.HasValue)
        {
            return $"CPU {cpu.Value:0} °C, GPU {gpu.Value:0} °C.";
        }

        return cpu.HasValue ? $"CPU {cpu.Value:0} °C." : $"GPU {gpu ?? 0:0} °C.";
    }

    public HealthDomain AssessStorage(IReadOnlyList<StorageVolumeInfo> volumes)
    {
        if (volumes.Count == 0)
        {
            return new HealthDomain("Storage", HealthStatus.Unknown, "No volumes read",
                "No fixed volumes were enumerated.");
        }

        StorageVolumeInfo? tightest = volumes
            .Where(volume => volume.TotalBytes > 0)
            .OrderBy(volume => volume.FreeBytes / (double)volume.TotalBytes)
            .FirstOrDefault();

        if (tightest is null)
        {
            return new HealthDomain("Storage", HealthStatus.Unknown, "No usable volumes", "Volume sizes were not reported.");
        }

        double freePercent = tightest.FreeBytes * 100.0 / tightest.TotalBytes;
        double freeGb = tightest.FreeBytes / 1024d / 1024d / 1024d;

        if (freePercent <= DiskFreeActionPercent)
        {
            return new HealthDomain("Storage", HealthStatus.ActionNeeded, $"{tightest.RootPath} nearly full",
                $"{freeGb:0.0} GB free ({freePercent:0}%). Windows updates and the page file need headroom.");
        }

        if (freePercent <= DiskFreeAttentionPercent)
        {
            return new HealthDomain("Storage", HealthStatus.Attention, $"{tightest.RootPath} filling up",
                $"{freeGb:0.0} GB free ({freePercent:0}%). Worth clearing space soon.");
        }

        return new HealthDomain("Storage", HealthStatus.Healthy, $"{freePercent:0}% free on {tightest.RootPath}",
            $"{freeGb:0.0} GB available.");
    }

    public HealthDomain AssessNetwork(bool connected, string summary)
    {
        if (!connected)
        {
            return new HealthDomain("Network", HealthStatus.ActionNeeded, "Not connected",
                "No adapter holds a routable IPv4 address.");
        }

        return new HealthDomain("Network", HealthStatus.Healthy, "Connected",
            string.IsNullOrWhiteSpace(summary) ? "An adapter has a routable address." : summary);
    }

    public HealthDomain AssessMemory(double usagePercent)
    {
        if (usagePercent >= 90)
        {
            return new HealthDomain("Memory", HealthStatus.ActionNeeded, $"{usagePercent:0}% used",
                "Very little free memory. Expect paging and slow switching between apps.");
        }

        if (usagePercent >= 80)
        {
            return new HealthDomain("Memory", HealthStatus.Attention, $"{usagePercent:0}% used",
                "Memory is tight under the current workload.");
        }

        return new HealthDomain("Memory", HealthStatus.Healthy, $"{usagePercent:0}% used", "Comfortable headroom.");
    }

    /// <summary>
    /// The single worst status across the domains, which is what the summary line reports.
    /// </summary>
    public static HealthStatus Overall(IEnumerable<HealthDomain> domains)
    {
        HealthStatus worst = HealthStatus.Healthy;
        foreach (HealthDomain domain in domains)
        {
            if (domain.Status == HealthStatus.ActionNeeded)
            {
                return HealthStatus.ActionNeeded;
            }

            if (domain.Status == HealthStatus.Attention)
            {
                worst = HealthStatus.Attention;
            }
        }

        return worst;
    }

    public static string DescribeOverall(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "No problems found",
        HealthStatus.Attention => "Worth a look",
        HealthStatus.ActionNeeded => "Action needed",
        _ => "Checking..."
    };
}
