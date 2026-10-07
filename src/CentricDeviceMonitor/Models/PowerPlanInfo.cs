namespace CentricDeviceMonitor.Models;

public sealed class PowerPlanInfo
{
    public string SchemeGuid { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool IsActive { get; init; }
}
