using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CentricDeviceMonitor.ServiceHost;

/// <summary>
/// "Open the dashboard after sign-in" for a standard user: the sign-in task is the one the installer creates (it runs
/// with the highest rights the user has, so administrators get an elevated dashboard without a prompt), and only an
/// administrator or this service may create or delete it.
/// </summary>
internal static class DashboardStartupTask
{
    public const string TaskName = "Windows Utility by Sajith Dashboard";

    public static async Task<string> SetAsync(string account, bool enable, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(account, @"^[^\\/""]+\\[^\\/""]+$"))
        {
            throw new InvalidOperationException("The Windows account that asked could not be identified.");
        }

        string? owner = await TaskUserAsync(cancellationToken);
        if (!enable)
        {
            if (owner is null)
            {
                return "The dashboard was not set to open after sign-in.";
            }

            if (!string.Equals(owner, account, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The sign-in task belongs to {owner}, so it was left alone.");
            }

            await RunAsync($"/Delete /TN \"{TaskName}\" /F", cancellationToken);
            return "The dashboard no longer opens after sign-in.";
        }

        string executable = Path.Combine(AppContext.BaseDirectory, "WindowsUtilityBySajith.exe");
        (int exitCode, string output) = await RunAsync(
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{executable}\\\" --startup\" /SC ONLOGON /RU \"{account}\" /RL HIGHEST /IT /F", cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Windows could not create the sign-in task: {output.Trim()}");
        }

        return owner is not null && !string.Equals(owner, account, StringComparison.OrdinalIgnoreCase)
            ? $"The dashboard now opens after {account} signs in (instead of {owner})."
            : "The dashboard now opens after sign-in.";
    }

    /// <summary>The account the task runs as ("OFFICE\maria"), or null when there is no task.</summary>
    private static async Task<string?> TaskUserAsync(CancellationToken cancellationToken)
    {
        (int exitCode, string xml) = await RunAsync($"/Query /TN \"{TaskName}\" /XML", cancellationToken);
        if (exitCode != 0)
        {
            return null;
        }

        Match user = Regex.Match(xml, "<UserId>([^<]+)</UserId>");
        if (!user.Success)
        {
            return string.Empty;
        }

        string value = user.Groups[1].Value.Trim();
        if (value.StartsWith("S-1-", StringComparison.Ordinal))
        {
            try
            {
                return new System.Security.Principal.SecurityIdentifier(value).Translate(typeof(System.Security.Principal.NTAccount)).Value;
            }
            catch (SystemException)
            {
                return value;
            }
        }

        return value;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("schtasks could not be started.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, (await output) + (await error));
    }
}
