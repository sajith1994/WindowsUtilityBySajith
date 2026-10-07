using System.ServiceProcess;

namespace CentricDeviceMonitor.ServiceHost;

internal sealed class MonitoringWindowsService : ServiceBase
{
    private readonly MonitoringEngine _engine = new();

    public MonitoringWindowsService()
    {
        ServiceName = "CentricDeviceMonitorService";
        CanStop = true;
        CanShutdown = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        _engine.Start();
    }

    protected override void OnStop()
    {
        _engine.StopAsync().GetAwaiter().GetResult();
    }

    protected override void OnShutdown()
    {
        _engine.StopAsync().GetAwaiter().GetResult();
        base.OnShutdown();
    }

    public void StartInteractive() => OnStart(Array.Empty<string>());

    public void StopInteractive() => OnStop();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.Dispose();
        }

        base.Dispose(disposing);
    }
}
