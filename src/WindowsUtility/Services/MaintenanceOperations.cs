using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WindowsUtility.Models;
using Microsoft.Win32;

namespace WindowsUtility.Services;

/// <summary>
/// Whether standard users may run the maintenance tools without an administrator password (through the background
/// service). Kept in HKLM, which only administrators can change, so a standard user cannot turn it on for themselves;
/// the installer sets it (on by default) and an administrator can change it in the dashboard.
/// </summary>
public static class MaintenancePolicy
{
    public const string RegistryKeyPath = @"SOFTWARE\Windows Utility";
    public const string StandardUserMaintenanceValue = "StandardUserMaintenance";

    public static bool StandardUsersMayMaintain()
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(RegistryKeyPath);
            return key?.GetValue(StandardUserMaintenanceValue) is not int value || value != 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    /// <summary>Needs administrator rights.</summary>
    public static void Set(bool allowed)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey key = baseKey.CreateSubKey(RegistryKeyPath, writable: true);
        key.SetValue(StandardUserMaintenanceValue, allowed ? 1 : 0, RegistryValueKind.DWord);
    }
}

/// <summary>The outcome of a maintenance command, with the commands' output for the technician.</summary>
public sealed record MaintenanceResult(bool Ok, string Message, string Output = "");

/// <summary>
/// Maintenance that needs administrator rights, shared by the dashboard (as an administrator) and the background
/// service (for standard users). Only fixed commands are run; the arguments are validated first.
/// </summary>
public static class MaintenanceOperations
{
    public static async Task<MaintenanceResult> NetworkRepairAsync(string kind, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(string File, string Arguments)> commands = NetworkRepairKinds.Commands(kind);
        if (commands.Count == 0)
        {
            return new MaintenanceResult(false, $"Unknown network repair \"{kind}\".");
        }

        StringBuilder output = new();
        bool allOk = true;
        foreach ((string file, string arguments) in commands)
        {
            (int exitCode, string text) = await RunAsync(file, arguments, TimeSpan.FromMinutes(2), cancellationToken);
            output.AppendLine($"> {Path.GetFileNameWithoutExtension(file)} {arguments}");
            output.AppendLine(text.Trim());
            output.AppendLine();
            // "ipconfig /release" fails on adapters without DHCP; the rest of the repair still runs.
            allOk &= exitCode == 0;
        }

        bool restart = kind is NetworkRepairKinds.ResetStack or NetworkRepairKinds.Full;
        string message = (allOk ? "Done." : "Done, with warnings (see the output).") + (restart ? " Restart Windows for the reset to take full effect." : string.Empty);
        return new MaintenanceResult(true, message, output.ToString().Trim());
    }

    public static string SpeedTestRuleName(int port) => $"Windows Utility LAN Speed Test TCP {port}";

    public static async Task<MaintenanceResult> AllowSpeedTestPortAsync(int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1024 or > 65535)
        {
            return new MaintenanceResult(false, "The port must be between 1024 and 65535.");
        }

        string rule = SpeedTestRuleName(port);
        await RunAsync("netsh.exe", $"advfirewall firewall delete rule name=\"{rule}\"", TimeSpan.FromSeconds(30), cancellationToken);
        (int exitCode, string output) = await RunAsync("netsh.exe",
            $"advfirewall firewall add rule name=\"{rule}\" dir=in action=allow protocol=TCP localport={port} profile=private,domain",
            TimeSpan.FromSeconds(30), cancellationToken);
        return exitCode == 0
            ? new MaintenanceResult(true, $"Windows Firewall now allows TCP {port} on Private and Domain networks.")
            : new MaintenanceResult(false, $"Windows Firewall did not accept the rule: {output.Trim()}");
    }

    /// <summary>The Windows drive from the boot configuration ("C:"), or an empty result when it cannot be read.</summary>
    public static async Task<MaintenanceResult> DetectWindowsDriveAsync(CancellationToken cancellationToken = default)
    {
        (int exitCode, string output) = await RunAsync("bcdedit.exe", "/enum {current}", TimeSpan.FromSeconds(30), cancellationToken);
        Match match = Regex.Match(output, @"^\s*osdevice\s+partition=([A-Za-z]:)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return exitCode == 0 && match.Success
            ? new MaintenanceResult(true, match.Groups[1].Value.ToUpperInvariant())
            : new MaintenanceResult(false, "The boot configuration did not name the Windows drive.");
    }

    /// <summary>Runs a program hidden and returns its exit code and combined output.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string file, string arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = new(file, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{file} could not be started.");
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        Task<string> output = process.StandardOutput.ReadToEndAsync(limit.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, $"{file} took longer than {timeout.TotalSeconds:0} seconds and was stopped.");
        }

        return (process.ExitCode, ((await output) + Environment.NewLine + (await error)).Trim());
    }
}
