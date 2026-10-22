using System.Net;
using System.Text;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed record PcIssue(string Text, bool Serious);

public sealed record DailySummary(string Headline, string Message, string Html, int PcsNeedingAttention);

/// <summary>
/// The morning summary of the monitored PCs: which need attention now (offline, low disk, protection off, updates
/// overdue, crashes, worn battery, no recent restore point...), what was announced in the last 24 hours, and printers
/// low on ink or toner. Shown as one notification that opens a printable page.
/// </summary>
public static class DailySummaryService
{
    /// <summary>What is wrong with a PC now, most serious first. Also used for the phone page.</summary>
    public static List<PcIssue> Issues(RemotePcView view)
    {
        List<PcIssue> issues = new();
        if (!view.IsOnline)
        {
            issues.Add(new PcIssue(view.LastSeen is DateTime seen ? $"Offline since {seen:ddd HH:mm}" : $"Not reachable: {view.StateMessage}", true));
            return issues;
        }

        if (view.Stats is not { } stats)
        {
            return issues;
        }

        if (view.IsHot)
        {
            issues.Add(new PcIssue($"Running hot ({view.TemperatureText})", true));
        }

        foreach (RemoteDiskView disk in view.Disks.Where(disk => disk.IsLow))
        {
            issues.Add(new PcIssue($"Drive {disk.Name} nearly full ({disk.Detail})", true));
        }

        if (stats.RamUsagePercent >= RemoteAlertTracker.MemoryThresholdPercent)
        {
            issues.Add(new PcIssue($"Memory at {stats.RamUsagePercent:0}%", false));
        }

        if (view.ProtectionAtRisk)
        {
            issues.Add(new PcIssue(view.ProtectionText, true));
        }

        if (view.Security is { } security)
        {
            if (RemotePcView.UpdatesStale(security))
            {
                issues.Add(new PcIssue($"No Windows update since {security.LastUpdateInstalledUtc?.ToLocalTime():d MMM}", false));
            }
            else if (security.RestartRequired)
            {
                issues.Add(new PcIssue("Restart needed to finish Windows updates", false));
            }
        }

        if (stats.Problems is { HasSerious: true } problems)
        {
            issues.Add(new PcIssue($"Last 7 days: {problems.Describe()}", true));
        }

        if (view.BatteryWorn)
        {
            issues.Add(new PcIssue($"Battery worn ({stats.BatteryHealthPercent:0}% health)", false));
        }

        if (stats.Recovery is { } recovery && RemotePcView.RecoveryStale(recovery))
        {
            issues.Add(new PcIssue(recovery.SystemProtectionOn == false
                ? "System Protection off (no restore points)"
                : $"No restore point in {RemotePcView.RestorePointStaleDays} days", false));
        }

        return issues;
    }

    public static DailySummary Build(
        IReadOnlyList<RemotePcView> views,
        IReadOnlyList<RemoteAlert> alerts,
        IReadOnlyList<(string Name, PrinterSuppliesResult Result)> printers,
        DateTime now,
        string generatedBy)
    {
        List<(RemotePcView View, List<PcIssue> Issues)> rows = views
            .Select(view => (view, Issues(view)))
            .OrderByDescending(row => row.Item2.Any(issue => issue.Serious))
            .ThenByDescending(row => row.Item2.Count)
            .ThenBy(row => row.view.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        List<(RemotePcView View, List<PcIssue> Issues)> attention = rows.Where(row => row.Issues.Count > 0).ToList();
        int online = views.Count(view => view.IsOnline);
        List<string> offline = views.Where(view => !view.IsOnline).Select(view => view.Title).ToList();
        List<RemoteAlert> problems = alerts.Where(alert => !alert.IsRecovery).ToList();
        List<(string Name, PrinterSuppliesResult Result)> lowPrinters = printers.Where(printer => printer.Result.NeedsAttention).ToList();

        string headline = attention.Count == 0
            ? $"Daily summary: all {views.Count} PC{(views.Count == 1 ? " is" : "s are")} fine"
            : $"Daily summary: {attention.Count} of {views.Count} PC{(views.Count == 1 ? "" : "s")} need{(attention.Count == 1 ? "s" : "")} attention";

        List<string> lines = new();
        if (offline.Count > 0)
        {
            lines.Add($"Offline: {Shorten(offline)}");
        }

        List<string> others = attention.Where(row => row.View.IsOnline).Select(row => row.View.Title).ToList();
        if (others.Count > 0)
        {
            lines.Add($"Check: {Shorten(others)}");
        }

        lines.Add(problems.Count == 0 ? "No alerts in the last 24 hours." : $"{problems.Count} alert{(problems.Count == 1 ? "" : "s")} in the last 24 hours.");
        if (lowPrinters.Count > 0)
        {
            lines.Add($"Low ink or toner: {Shorten(lowPrinters.Select(printer => printer.Name).ToList())}");
        }

        lines.Add("Click for the full summary.");

        StringBuilder html = new();
        html.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Daily summary</title>
            <style>
              body { font-family: "Segoe UI", Arial, sans-serif; color: #1d1d1f; margin: 24px; max-width: 980px; }
              h1 { font-size: 22px; margin: 0 0 4px; } h2 { font-size: 16px; margin: 24px 0 8px; } .muted { color: #6e6e73; font-size: 12px; }
              .summary { display: flex; flex-wrap: wrap; gap: 10px; margin: 16px 0; }
              .card { border: 1px solid #ddd; border-radius: 8px; padding: 8px 14px; min-width: 120px; }
              .card b { display: block; font-size: 20px; } .card span { font-size: 12px; color: #6e6e73; }
              .card.warn b { color: #c76a00; } .card.bad b { color: #d70015; }
              table { border-collapse: collapse; font-size: 13px; width: 100%; }
              th, td { border-bottom: 1px solid #e5e5ea; padding: 6px 8px; text-align: left; vertical-align: top; }
              th { color: #6e6e73; font-weight: 600; font-size: 12px; }
              .bad { color: #d70015; } .warn { color: #c76a00; } .ok { color: #1a7f37; }
              ul { margin: 0; padding-left: 18px; }
            </style></head><body>
            """);
        html.Append($"<h1>{Encode(headline.Replace("Daily summary: ", string.Empty))}</h1>");
        html.Append($"<div class=\"muted\">Daily summary of {views.Count} monitored PCs  •  {now:dddd d MMMM yyyy HH:mm}  •  from {Encode(generatedBy)}  •  Windows Utility {UpdateService.GetCurrentVersion().ToString(3)}</div>");
        html.Append("<div class=\"summary\">");
        Card(html, online, $"online of {views.Count}", offline.Count > 0 ? "warn" : string.Empty);
        Card(html, attention.Count, "need attention", attention.Any(row => row.Issues.Any(issue => issue.Serious)) ? "bad" : attention.Count > 0 ? "warn" : string.Empty);
        Card(html, problems.Count, "alerts in 24 hours", problems.Count > 0 ? "warn" : string.Empty);
        if (printers.Count > 0)
        {
            Card(html, lowPrinters.Count, "printers low on ink or toner", lowPrinters.Count > 0 ? "warn" : string.Empty);
        }

        html.Append("</div>");

        html.Append("<h2>Needs attention</h2>");
        if (attention.Count == 0)
        {
            html.Append("<p class=\"ok\">Nothing needs attention right now.</p>");
        }
        else
        {
            html.Append("<table><tr><th>PC</th><th>What to check</th></tr>");
            foreach ((RemotePcView view, List<PcIssue> issues) in attention)
            {
                html.Append($"<tr><td><b>{Encode(view.Title)}</b>{(view.HasGroup ? $"<br><span class=\"muted\">{Encode(view.Group)}</span>" : string.Empty)}</td><td><ul>");
                foreach (PcIssue issue in issues)
                {
                    html.Append($"<li class=\"{(issue.Serious ? "bad" : "warn")}\">{Encode(issue.Text)}</li>");
                }

                html.Append("</ul></td></tr>");
            }

            html.Append("</table>");
        }

        html.Append("<h2>Last 24 hours</h2>");
        if (alerts.Count == 0)
        {
            html.Append("<p class=\"muted\">No alerts. (Alerts are only noticed while the dashboard is running on this PC, also in the notification area.)</p>");
        }
        else
        {
            html.Append("<table><tr><th>When</th><th>PC</th><th>What happened</th></tr>");
            foreach (RemoteAlert alert in alerts.OrderByDescending(alert => alert.At))
            {
                html.Append($"<tr><td>{alert.At:ddd HH:mm}</td><td>{Encode(alert.PcName)}</td><td class=\"{(alert.IsRecovery ? "ok" : "warn")}\">{Encode(alert.Title)}<br><span class=\"muted\">{Encode(alert.Message)}</span></td></tr>");
            }

            html.Append("</table>");
        }

        if (printers.Count > 0)
        {
            html.Append("<h2>Printers</h2><table><tr><th>Printer</th><th>Supplies</th></tr>");
            foreach ((string name, PrinterSuppliesResult result) in printers.OrderByDescending(printer => printer.Result.NeedsAttention))
            {
                string supplies = result.Supplies.Count == 0
                    ? $"<span class=\"muted\">{Encode(result.Error ?? "No levels reported.")}</span>"
                    : string.Join("<br>", result.Supplies.Select(supply =>
                        $"<span class=\"{(supply.State is SupplyState.Low or SupplyState.Empty ? supply.State == SupplyState.Empty ? "bad" : "warn" : string.Empty)}\">{Encode(supply.Name)}: {Encode(PrinterSuppliesService.Describe(supply))}</span>"));
                html.Append($"<tr><td>{Encode(name)}<br><span class=\"muted\">{Encode(result.Address)}</span></td><td>{supplies}</td></tr>");
            }

            html.Append("</table>");
        }

        html.Append("<h2>All PCs</h2><table><tr><th>PC</th><th>Status</th><th>CPU</th><th>Memory</th><th>Fullest drive</th><th>Signed in</th></tr>");
        foreach ((RemotePcView view, List<PcIssue> issues) in rows.OrderBy(row => row.View.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            RemoteDiskView? fullest = view.Disks.OrderByDescending(disk => disk.UsedPercent).FirstOrDefault();
            string status = view.IsOnline ? issues.Count == 0 ? "<span class=\"ok\">Fine</span>" : $"<span class=\"warn\">{issues.Count} to check</span>" : "<span class=\"bad\">Offline</span>";
            html.Append($"<tr><td>{Encode(view.Title)}</td><td>{status}</td><td>{(view.IsOnline ? view.CpuText : string.Empty)}</td><td>{(view.IsOnline ? view.RamText : string.Empty)}</td>"
                + $"<td>{(fullest is null ? string.Empty : Encode($"{fullest.Name} {fullest.PercentText} used"))}</td><td>{Encode(view.IsOnline ? view.SessionsText.Replace("Signed in: ", string.Empty) : string.Empty)}</td></tr>");
        }

        html.Append("</table><p class=\"muted\">To save as PDF, print this page and choose \"Microsoft Print to PDF\".</p></body></html>");
        return new DailySummary(headline, string.Join("\n", lines), html.ToString(), attention.Count);
    }

    private static string Shorten(List<string> names) =>
        names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";

    private static void Card(StringBuilder html, int value, string label, string level) =>
        html.Append($"<div class=\"card {level}\"><b>{value}</b><span>{Encode(label)}</span></div>");

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
}
