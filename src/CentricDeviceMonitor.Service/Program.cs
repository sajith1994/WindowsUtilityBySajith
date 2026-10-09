using System.ServiceProcess;

namespace CentricDeviceMonitor.ServiceHost;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (Environment.UserInteractive && args.Any(arg => string.Equals(arg, "--console", StringComparison.OrdinalIgnoreCase)))
        {
            using MonitoringWindowsService service = new();
            service.StartInteractive();
            Console.WriteLine("Windows Utility Service is running in console mode. Press Enter to stop.");
            Console.ReadLine();
            service.StopInteractive();
            return;
        }

        ServiceBase.Run(new MonitoringWindowsService());
    }
}
