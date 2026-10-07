using System.IO;
using System.Text;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.ServiceHost;

internal static class ServiceLog
{
    private static readonly object SyncRoot = new();

    public static void Write(string area, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                SharedDataPaths.EnsureDirectories();
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{area}] {message}{Environment.NewLine}";
                File.AppendAllText(SharedDataPaths.ServiceLogFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Service logging must never terminate monitoring.
        }
    }

    public static void WriteException(string area, Exception exception) => Write(area, exception.ToString());
}
