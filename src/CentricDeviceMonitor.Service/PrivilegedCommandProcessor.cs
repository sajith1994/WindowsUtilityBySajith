using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.ServiceHost;

/// <summary>
/// Does the administrator-only work the dashboard asks for, so the dashboard can run as a standard user. Requests
/// are JSON files in the shared commands folder; the service checks each every half second, works out who asked from
/// the file's owner (set by Windows, not by the sender), refuses maintenance when an administrator turned it off for
/// standard users, builds the commands itself from validated arguments and writes a response file.
/// </summary>
internal sealed class PrivilegedCommandProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ResponseLifetime = TimeSpan.FromMinutes(10);

    private readonly string _requestsDirectory;
    private readonly string _responsesDirectory;
    private readonly Func<bool> _standardUsersMayMaintain;
    private readonly Func<PrivilegedServiceCommandRequest, string, CancellationToken, Task<(string Message, string Data)>> _execute;
    private readonly RemoteUpdater? _updater;

    /// <param name="updater">Installs the verified official release (shared with updates started from another PC).</param>
    /// <param name="execute">Tests pass a stand-in so nothing is really installed or reset.</param>
    public PrivilegedCommandProcessor(RemoteUpdater? updater = null, string? requestsDirectory = null, string? responsesDirectory = null,
        Func<bool>? standardUsersMayMaintain = null,
        Func<PrivilegedServiceCommandRequest, string, CancellationToken, Task<(string Message, string Data)>>? execute = null)
    {
        _updater = updater;
        _requestsDirectory = requestsDirectory ?? SharedDataPaths.PrivilegedCommandRequestsDirectory;
        _responsesDirectory = responsesDirectory ?? SharedDataPaths.PrivilegedCommandResponsesDirectory;
        _standardUsersMayMaintain = standardUsersMayMaintain ?? MaintenancePolicy.StandardUsersMayMaintain;
        _execute = execute ?? ExecuteAsync;
    }

    /// <summary>Checks for requests every half second until the service stops (separate from the slower monitoring loop).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        DateTime cleanedAt = DateTime.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAsync(cancellationToken);
                if (DateTime.UtcNow - cleanedAt > TimeSpan.FromMinutes(5))
                {
                    RemoveStaleResponses();
                    cleanedAt = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ServiceLog.WriteException("Privileged command loop", exception);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        IEnumerable<string> requestFiles;
        try
        {
            Directory.CreateDirectory(_requestsDirectory);
            Directory.CreateDirectory(_responsesDirectory);
            requestFiles = Directory
                .EnumerateFiles(_requestsDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(File.GetCreationTimeUtc)
                .Take(20)
                .ToArray();
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("Privileged command scan", exception);
            return;
        }

        foreach (string requestPath in requestFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessOneAsync(requestPath, cancellationToken);
        }
    }

    private async Task ProcessOneAsync(string requestPath, CancellationToken cancellationToken)
    {
        PrivilegedServiceCommandRequest? request = null;
        string requester = RequestedBy(requestPath);
        try
        {
            // The file's own time, not a time the sender wrote into it.
            DateTime writtenUtc = File.GetLastWriteTimeUtc(requestPath);
            await using (FileStream stream = new(requestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                request = await JsonSerializer.DeserializeAsync<PrivilegedServiceCommandRequest>(stream, JsonOptions, cancellationToken);
            }

            if (request is null || request.Id == Guid.Empty)
            {
                return;
            }

            request.Arguments ??= new Dictionary<string, string>();
            if (DateTime.UtcNow - writtenUtc > RequestLifetime)
            {
                await WriteResponseAsync(request.Id, false, "The request waited too long and was not carried out. Try again.", string.Empty, cancellationToken);
                return;
            }

            if (PrivilegedServiceActions.Maintenance.Contains(request.Action) && !_standardUsersMayMaintain())
            {
                ServiceLog.Write("Command", $"{request.Action} requested by {requester} refused: maintenance is turned off for standard users.");
                await WriteResponseAsync(request.Id, false,
                    "An administrator has turned off maintenance tools for standard users on this PC. Ask an administrator, or open Windows Utility as administrator.",
                    string.Empty, cancellationToken);
                return;
            }

            (string message, string data) = await _execute(request, requester, cancellationToken);
            ServiceLog.Write("Command", $"{request.Action} requested by {requester}: {message}");
            await WriteResponseAsync(request.Id, true, message, data, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException($"Privileged command ({request?.Action} by {requester})", exception);
            if (request is not null && request.Id != Guid.Empty)
            {
                try
                {
                    await WriteResponseAsync(request.Id, false, exception.Message, string.Empty, cancellationToken);
                }
                catch
                {
                }
            }
        }
        finally
        {
            TryDelete(requestPath);
        }
    }

    /// <summary>"OFFICE\maria" from the request file's owner, which Windows sets to whoever created the file.</summary>
    internal static string RequestedBy(string path)
    {
        try
        {
            IdentityReference? owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            if (owner is SecurityIdentifier sid)
            {
                try
                {
                    return sid.Translate(typeof(NTAccount)).Value;
                }
                catch (IdentityNotMappedException)
                {
                    return sid.Value;
                }
            }
        }
        catch (SystemException)
        {
            // No owner could be read (the file was already removed).
        }

        return "unknown user";
    }

    private async Task<(string Message, string Data)> ExecuteAsync(PrivilegedServiceCommandRequest request, string requester, CancellationToken cancellationToken)
    {
        Dictionary<string, string> arguments = request.Arguments;
        string Argument(string name) => arguments.TryGetValue(name, out string? value) ? value ?? string.Empty : string.Empty;

        switch (request.Action)
        {
            case PrivilegedServiceActions.StartPrintSpooler:
                return (await Task.Run(StartSpooler, cancellationToken), string.Empty);
            case PrivilegedServiceActions.StopPrintSpooler:
                return (await Task.Run(StopSpooler, cancellationToken), string.Empty);
            case PrivilegedServiceActions.RestartPrintSpooler:
                return (await Task.Run(RestartSpooler, cancellationToken), string.Empty);

            case PrivilegedServiceActions.InstallUpdate:
            {
                RemoteUpdater updater = _updater ?? throw new InvalidOperationException("Updating is not available in this service.");
                RemoteUpdateResult result = await updater.StartAsync($"{requester} on this PC");
                return (result.Message, JsonSerializer.Serialize(result));
            }

            case PrivilegedServiceActions.InstallPrinter:
            {
                string[] services = Argument("services").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(service => service is "IPP" or "RAW" or "LPD").ToArray();
                PrinterInstallResult result = await PrinterOperations.InstallAsync(Argument("address"), services, Limit(Argument("model"), 120),
                    Limit(Argument("hostName"), 255), cancellationToken);
                return (result.Message, JsonSerializer.Serialize(result));
            }

            case PrivilegedServiceActions.RemovePrinter:
            {
                string name = Argument("name").Trim();
                if (name.Length is 0 or > 260)
                {
                    throw new InvalidOperationException("The printer name is missing or too long.");
                }

                PrinterRemoveResult result = await PrinterOperations.RemoveAsync(name, Limit(Argument("port"), 260),
                    Argument("removePort") == "true", Argument("force") == "true", cancellationToken);
                return (result.Message, JsonSerializer.Serialize(result));
            }

            case PrivilegedServiceActions.ClearWindowsTemp:
            {
                TemporaryFileCleanupResult result = await new TemporaryFileCleanupService().CleanAsync(includeUserTemp: false, includeWindowsTemp: true, cancellationToken);
                return ($"Windows Temp: {result.FilesDeleted:N0} files removed, {result.SizeDisplay} freed.", JsonSerializer.Serialize(result));
            }

            case PrivilegedServiceActions.NetworkRepair:
            {
                MaintenanceResult result = await MaintenanceOperations.NetworkRepairAsync(Argument("kind"), cancellationToken);
                return (result.Message, JsonSerializer.Serialize(result));
            }

            case PrivilegedServiceActions.AllowSpeedTestPort:
            {
                MaintenanceResult result = await MaintenanceOperations.AllowSpeedTestPortAsync(int.TryParse(Argument("port"), out int port) ? port : 0, cancellationToken);
                return result.Ok ? (result.Message, JsonSerializer.Serialize(result)) : throw new InvalidOperationException(result.Message);
            }

            case PrivilegedServiceActions.SetDashboardStartup:
                return (await DashboardStartupTask.SetAsync(requester, Argument("enable") == "true", cancellationToken), string.Empty);

            case PrivilegedServiceActions.DetectWindowsDrive:
            {
                MaintenanceResult result = await MaintenanceOperations.DetectWindowsDriveAsync(cancellationToken);
                return (result.Message, JsonSerializer.Serialize(result));
            }

            default:
                throw new InvalidOperationException($"The requested action \"{request.Action}\" is not supported by this version of the background service.");
        }
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

    private static string StartSpooler()
    {
        using ServiceController controller = new("Spooler");
        controller.Refresh();

        if (controller.Status == ServiceControllerStatus.Running)
        {
            return "Print Spooler is already running.";
        }

        if (controller.Status == ServiceControllerStatus.StopPending)
        {
            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }

        controller.Refresh();
        if (controller.Status != ServiceControllerStatus.Running &&
            controller.Status != ServiceControllerStatus.StartPending)
        {
            controller.Start();
        }

        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        return "Print Spooler started by the Windows Utility background service.";
    }

    private static string StopSpooler()
    {
        using ServiceController controller = new("Spooler");
        controller.Refresh();

        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            return "Print Spooler is already stopped.";
        }

        if (controller.Status == ServiceControllerStatus.StartPending)
        {
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        }

        controller.Refresh();
        if (controller.Status != ServiceControllerStatus.Stopped &&
            controller.Status != ServiceControllerStatus.StopPending)
        {
            if (!controller.CanStop)
            {
                throw new InvalidOperationException("Windows reports that the Print Spooler cannot currently be stopped.");
            }

            controller.Stop();
        }

        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        return "Print Spooler stopped by the Windows Utility background service.";
    }

    private static string RestartSpooler()
    {
        using ServiceController controller = new("Spooler");
        controller.Refresh();

        if (controller.Status != ServiceControllerStatus.Stopped)
        {
            if (controller.Status == ServiceControllerStatus.StartPending)
            {
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }

            controller.Refresh();
            if (controller.Status != ServiceControllerStatus.Stopped &&
                controller.Status != ServiceControllerStatus.StopPending)
            {
                if (!controller.CanStop)
                {
                    throw new InvalidOperationException("Windows reports that the Print Spooler cannot currently be stopped.");
                }

                controller.Stop();
            }

            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }

        controller.Refresh();
        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        return "Print Spooler restarted by the Windows Utility background service.";
    }

    private async Task WriteResponseAsync(Guid requestId, bool success, string message, string data, CancellationToken cancellationToken)
    {
        PrivilegedServiceCommandResponse response = new()
        {
            Id = requestId,
            Success = success,
            Message = message,
            Data = data,
            CompletedAt = DateTime.UtcNow
        };

        string responsePath = Path.Combine(_responsesDirectory, $"{requestId:N}.json");
        string temporaryPath = responsePath + ".tmp";

        await using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(stream, response, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, responsePath, overwrite: true);
    }

    /// <summary>Responses nobody collected (the dashboard closed or gave up waiting).</summary>
    private void RemoveStaleResponses()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_responsesDirectory))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > ResponseLifetime)
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
