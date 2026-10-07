using System.IO;
using System.ServiceProcess;
using System.Text.Json;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.ServiceHost;

internal sealed class PrivilegedCommandProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        SharedDataPaths.EnsureDirectories();

        IEnumerable<string> requestFiles;
        try
        {
            requestFiles = Directory
                .EnumerateFiles(SharedDataPaths.PrivilegedCommandRequestsDirectory, "*.json", SearchOption.TopDirectoryOnly)
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

    private static async Task ProcessOneAsync(string requestPath, CancellationToken cancellationToken)
    {
        PrivilegedServiceCommandRequest? request = null;
        try
        {
            await using (FileStream stream = new(
                             requestPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete))
            {
                request = await JsonSerializer.DeserializeAsync<PrivilegedServiceCommandRequest>(
                    stream,
                    JsonOptions,
                    cancellationToken);
            }

            if (request is null || request.Id == Guid.Empty)
            {
                return;
            }

            if (DateTime.Now - request.CreatedAt > TimeSpan.FromMinutes(2))
            {
                await WriteResponseAsync(request.Id, false, "The command expired before it could be processed.", cancellationToken);
                return;
            }

            string message = await ExecuteAsync(request.Action, cancellationToken);
            ServiceLog.Write("Command", message);
            await WriteResponseAsync(request.Id, true, message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("Privileged command", exception);
            if (request is not null && request.Id != Guid.Empty)
            {
                try
                {
                    await WriteResponseAsync(request.Id, false, exception.Message, cancellationToken);
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

    private static Task<string> ExecuteAsync(string action, CancellationToken cancellationToken) =>
        action switch
        {
            PrivilegedServiceActions.StartPrintSpooler => Task.Run(() => StartSpooler(), cancellationToken),
            PrivilegedServiceActions.StopPrintSpooler => Task.Run(() => StopSpooler(), cancellationToken),
            PrivilegedServiceActions.RestartPrintSpooler => Task.Run(() => RestartSpooler(), cancellationToken),
            _ => throw new InvalidOperationException("The requested privileged action is not supported.")
        };

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

    private static async Task WriteResponseAsync(
        Guid requestId,
        bool success,
        string message,
        CancellationToken cancellationToken)
    {
        PrivilegedServiceCommandResponse response = new()
        {
            Id = requestId,
            Success = success,
            Message = message,
            CompletedAt = DateTime.Now
        };

        string responsePath = SharedDataPaths.GetPrivilegedCommandResponsePath(requestId);
        string temporaryPath = responsePath + ".tmp";

        await using (FileStream stream = new(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(stream, response, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, responsePath, overwrite: true);
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
