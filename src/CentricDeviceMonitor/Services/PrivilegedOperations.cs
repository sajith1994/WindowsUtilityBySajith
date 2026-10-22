using System.Security.Principal;
using System.Text.Json;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// The dashboard runs as the signed-in user. Work that needs administrator rights is done here directly when the
/// dashboard happens to have them, and otherwise sent to the LocalSystem background service.
/// </summary>
public static class PrivilegedOperations
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<bool> Administrator = new(() =>
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    /// <summary>The dashboard runs with administrator rights (elevated).</summary>
    public static bool IsAdministrator => Administrator.Value;

    /// <summary>Asks the background service to do the action; returns its message (throws with the reason when it fails).</summary>
    public static async Task<string> RunAsync(string action, IReadOnlyDictionary<string, string>? arguments = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return (await SendAsync(action, arguments, timeout, cancellationToken)).Message;
    }

    /// <summary>Asks the background service to do the action and returns its structured result.</summary>
    public static async Task<T> RunAsync<T>(string action, IReadOnlyDictionary<string, string>? arguments = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        Models.PrivilegedServiceCommandResponse response = await SendAsync(action, arguments, timeout, cancellationToken);
        return JsonSerializer.Deserialize<T>(response.Data, JsonOptions)
            ?? throw new InvalidOperationException("The background service sent an empty answer.");
    }

    private static Task<Models.PrivilegedServiceCommandResponse> SendAsync(string action, IReadOnlyDictionary<string, string>? arguments, TimeSpan? timeout,
        CancellationToken cancellationToken) =>
        new PrivilegedCommandClient().SendAsync(action, arguments, timeout, cancellationToken);
}
