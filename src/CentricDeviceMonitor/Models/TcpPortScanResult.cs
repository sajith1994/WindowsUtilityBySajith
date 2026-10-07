namespace CentricDeviceMonitor.Models;

public sealed record TcpPortScanResult(int Port, string ServiceName)
{
    public string Display => string.IsNullOrWhiteSpace(ServiceName)
        ? Port.ToString()
        : $"{Port} {ServiceName}";
}
