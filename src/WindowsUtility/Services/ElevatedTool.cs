using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace WindowsUtility.Services;

/// <summary>
/// Tools that are risky enough to keep behind an administrator password (disk tools, the CPU and GPU load test,
/// changing adapter IP settings). A standard user's dashboard starts a second, elevated copy of the app that opens only
/// that tool, so Windows asks for an administrator only when the tool is used. Dashboards that already run as
/// administrator open the tools directly.
/// </summary>
public static class ElevatedTool
{
    public const string Argument = "--tool";
    public const string DiskTools = "disk-tools";
    public const string DiskClone = "disk-clone";
    public const string ThermalTest = "thermal-test";
    public const string ApplyAdapterSettings = "apply-adapter";

    private const int Cancelled = 1223;

    /// <summary>Opens a tool window elevated. Returns false when the administrator prompt was cancelled.</summary>
    public static bool Open(string tool)
    {
        try
        {
            using Process? process = Start(tool, string.Empty);
            return process is not null;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == Cancelled)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a tool without a window in an elevated copy: the request goes in a file, the copy writes its answer next
    /// to it. Returns null when the administrator prompt was cancelled.
    /// </summary>
    public static async Task<TResult?> RunAsync<TRequest, TResult>(string tool, TRequest request, TimeSpan timeout) where TResult : class
    {
        string folder = Path.Combine(SharedDataPaths.PrivilegedCommandsDirectory, "elevated");
        Directory.CreateDirectory(folder);
        string requestPath = Path.Combine(folder, $"{Guid.NewGuid():N}.json");
        string resultPath = ResultPath(requestPath);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request));
        try
        {
            using Process? process = Start(tool, $"\"{requestPath}\"");
            if (process is null)
            {
                return null;
            }

            using CancellationTokenSource limit = new(timeout);
            await process.WaitForExitAsync(limit.Token);
            return File.Exists(resultPath) ? JsonSerializer.Deserialize<TResult>(await File.ReadAllTextAsync(resultPath)) : null;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == Cancelled)
        {
            return null;
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    public static string ResultPath(string requestPath) => Path.ChangeExtension(requestPath, ".result.json");

    private static Process? Start(string tool, string extraArguments)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The application path could not be determined.");
        string arguments = $"{Argument} {tool} {extraArguments}".Trim();
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Started with "dotnet app.dll" while developing.
            arguments = $"\"{Path.Combine(AppContext.BaseDirectory, typeof(ElevatedTool).Assembly.GetName().Name + ".dll")}\" {arguments}";
        }

        return Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
