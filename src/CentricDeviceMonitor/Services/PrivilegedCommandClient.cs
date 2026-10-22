using System.IO;
using System.Text.Json;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// Sends an action to the LocalSystem background service: the request is a JSON file in the shared commands folder
/// (published with a rename so the service never sees half a file), and the service answers with a response file.
/// </summary>
public sealed class PrivilegedCommandClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _requestsDirectory;
    private readonly string _responsesDirectory;

    /// <param name="requestsDirectory">Tests use temporary folders; the dashboard uses the shared ones.</param>
    public PrivilegedCommandClient(string? requestsDirectory = null, string? responsesDirectory = null)
    {
        _requestsDirectory = requestsDirectory ?? SharedDataPaths.PrivilegedCommandRequestsDirectory;
        _responsesDirectory = responsesDirectory ?? SharedDataPaths.PrivilegedCommandResponsesDirectory;
    }

    /// <summary>Runs an action without arguments (the Print Spooler buttons).</summary>
    public async Task ExecuteAsync(string action, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        await SendAsync(action, null, timeout, cancellationToken);

    /// <summary>Runs an action and returns the service's answer; throws with the service's reason when it failed.</summary>
    public async Task<PrivilegedServiceCommandResponse> SendAsync(string action, IReadOnlyDictionary<string, string>? arguments = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("A privileged service action is required.", nameof(action));
        }

        Directory.CreateDirectory(_requestsDirectory);
        Directory.CreateDirectory(_responsesDirectory);

        Guid requestId = Guid.NewGuid();
        PrivilegedServiceCommandRequest request = new()
        {
            Id = requestId,
            Action = action,
            Arguments = arguments?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? new Dictionary<string, string>(),
            CreatedAt = DateTime.UtcNow
        };

        string requestPath = Path.Combine(_requestsDirectory, $"{requestId:N}.json");
        string responsePath = Path.Combine(_responsesDirectory, $"{requestId:N}.json");
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

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
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
                                ? "The background service could not complete the requested action."
                                : response.Message);
                    }

                    return response;
                }

                // The service picks a request up within a second; a request still waiting after 10 seconds means it
                // is not running (or busy with a long action), which the timeout below reports.
                await Task.Delay(150, cancellationToken);
            }
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(temporaryPath);
        }

        throw new TimeoutException(
            "The Windows Utility background service did not answer. Make sure it is running (System Stats > Background Monitoring), or open Windows Utility as administrator.");
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
