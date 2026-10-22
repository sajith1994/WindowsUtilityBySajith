using System.ServiceProcess;
using WindowsUtility.Services;

namespace WindowsUtility.ServiceHost;

internal static class Program
{
    private static void Main(string[] args)
    {
        // Normally the installer has already moved the shared folder; this catches anything it could not move.
        SharedDataPaths.MoveFromOldFolderName(Environment.SpecialFolder.CommonApplicationData);

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
