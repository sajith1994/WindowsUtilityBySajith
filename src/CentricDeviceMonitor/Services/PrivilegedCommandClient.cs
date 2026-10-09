using System.IO;
using System.Text.Json;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed class PrivilegedCommandClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task ExecuteAsync(string action, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("A privileged service action is required.", nameof(action));
        }

        SharedDataPaths.EnsureDirectories();

        Guid requestId = Guid.NewGuid();
        PrivilegedServiceCommandRequest request = new()
        {
            Id = requestId,
            Action = action,
            CreatedAt = DateTime.Now
        };

        string requestPath = SharedDataPaths.GetPrivilegedCommandRequestPath(requestId);
        string responsePath = SharedDataPaths.GetPrivilegedCommandResponsePath(requestId);
        string temporaryPath = requestPath + ".tmp";

        await using (FileStream stream = new(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(stream, request, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, requestPath, overwrite: true);

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(25);
        DateTime deadline = DateTime.UtcNow.Add(effectiveTimeout);

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (File.Exists(responsePath))
                {
                    PrivilegedServiceCommandResponse? response = await ReadResponseAsync(responsePath, cancellationToken);
                    if (response is null)
                    {
                        await Task.Delay(100, cancellationToken);
                        continue;
                    }

                    TryDelete(responsePath);
                    if (!response.Success)
                    {
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(response.Message)
                                ? "The background monitoring service could not complete the requested action."
                                : response.Message);
                    }

                    return;
                }

                await Task.Delay(100, cancellationToken);
            }
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(temporaryPath);
        }

        throw new TimeoutException(
            "The background monitoring service did not respond to the Print Spooler command. Make sure Windows Utility Service is running.");
    }

    private static async Task<PrivilegedServiceCommandResponse?> ReadResponseAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return await JsonSerializer.DeserializeAsync<PrivilegedServiceCommandResponse>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
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
