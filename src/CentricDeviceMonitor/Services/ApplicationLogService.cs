using System.IO;
using System.Text;

namespace CentricDeviceMonitor.Services;

public static class ApplicationLogService
{
    private static readonly object SyncRoot = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CentricDeviceMonitor",
        "logs");

    public static string LogFilePath => Path.Combine(LogDirectory, "application.log");

    public static void WriteMessage(string area, string message)
    {
        WriteLine(area, message);
    }

    public static void WriteException(string area, Exception exception)
    {
        StringBuilder details = new();
        details.AppendLine(exception.ToString());
        if (exception.InnerException is not null)
        {
            details.AppendLine("Inner exception:");
            details.AppendLine(exception.InnerException.ToString());
        }

        WriteLine(area, details.ToString().TrimEnd());
    }

    private static void WriteLine(string area, string details)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{area}] {details}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostic logging must never crash the monitoring application.
        }
    }
}
