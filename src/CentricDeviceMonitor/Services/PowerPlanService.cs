using System.Diagnostics;
using System.Text.RegularExpressions;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed partial class PowerPlanService
{
    public async Task<IReadOnlyList<PowerPlanInfo>> GetPowerPlansAsync()
    {
        string listOutput = await RunPowerCfgAsync("/list");
        string activeOutput = await RunPowerCfgAsync("/getactivescheme");
        string? activeGuid = ExtractFirstGuid(activeOutput);

        List<PowerPlanInfo> plans = new();
        foreach (string line in listOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = PowerSchemeLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            string schemeGuid = match.Groups["guid"].Value;
            string name = match.Groups["name"].Value.Trim();
            bool isActive = string.Equals(schemeGuid, activeGuid, StringComparison.OrdinalIgnoreCase) ||
                            match.Groups["active"].Success;

            plans.Add(new PowerPlanInfo
            {
                SchemeGuid = schemeGuid,
                Name = string.IsNullOrWhiteSpace(name) ? schemeGuid : name,
                IsActive = isActive
            });
        }

        if (plans.Count == 0 && !string.IsNullOrWhiteSpace(activeGuid))
        {
            plans.Add(new PowerPlanInfo
            {
                SchemeGuid = activeGuid,
                Name = ExtractPlanName(activeOutput) ?? "Active power plan",
                IsActive = true
            });
        }

        return plans
            .OrderByDescending(plan => plan.IsActive)
            .ThenBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task SetActivePowerPlanAsync(string schemeGuid)
    {
        if (!Guid.TryParse(schemeGuid, out Guid parsedGuid))
        {
            throw new ArgumentException("The selected power plan identifier is invalid.", nameof(schemeGuid));
        }

        await RunPowerCfgAsync($"/setactive {parsedGuid:D}");
    }

    public void OpenPowerOptions() =>
        StartControlPanel("/name Microsoft.PowerOptions");

    public void OpenLidAndPowerButtonOptions() =>
        StartControlPanel("/name Microsoft.PowerOptions /page pageGlobalSettings");

    private static async Task<string> RunPowerCfgAsync(string arguments)
    {
        ProcessStartInfo startInfo = new("powercfg.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows powercfg.exe could not be started.");

        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            string message = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? $"powercfg.exe failed with exit code {process.ExitCode}."
                    : message.Trim());
        }

        return output;
    }

    private static string? ExtractFirstGuid(string text)
    {
        Match match = GuidRegex().Match(text ?? string.Empty);
        return match.Success ? match.Value : null;
    }

    private static string? ExtractPlanName(string text)
    {
        Match match = ParenthesizedNameRegex().Match(text ?? string.Empty);
        return match.Success ? match.Groups["name"].Value.Trim() : null;
    }

    private static void StartControlPanel(string arguments)
    {
        Process.Start(new ProcessStartInfo("control.exe", arguments)
        {
            UseShellExecute = true
        });
    }

    [GeneratedRegex(@"(?<guid>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\s+\((?<name>.*)\)\s*(?<active>\*)?\s*$")]
    private static partial Regex PowerSchemeLineRegex();

    [GeneratedRegex(@"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"\((?<name>.*)\)")]
    private static partial Regex ParenthesizedNameRegex();
}
