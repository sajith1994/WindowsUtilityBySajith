using System.Threading;
using System.Windows.Forms;

namespace WindowsUtility.TrayHost;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using Mutex mutex = new(initiallyOwned: true, name: @"Local\WindowsUtility.ServiceTray", createdNew: out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new ServiceStatusTrayContext());
    }
}
