using System.Globalization;
using System.Net;
using System.Text;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// Inventory report of the monitored PCs: a CSV for Excel (one row per PC) or a printable HTML page with a summary
/// and highlighted problems. Offline PCs are listed with the last readings received and when they were last seen.
/// </summary>
public static class InventoryReportService
{
    private sealed record Row(RemotePcView View, RemoteStatsPayload? Stats, bool Online);

    private static readonly (string Header, Func<Row, string> Value)[] Columns =
    {
        ("PC", row => row.View.Title),
        ("Status", row => row.Online ? "Online" : "Offline"),
        ("Last seen", row => row.Online ? "Now" : row.View.LastSeen?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "Never this session"),
        ("Address", row => row.View.Entry.Host),
        ("Computer name", row => row.Stats?.MachineName ?? string.Empty),
        ("Manufacturer", row => row.Stats?.SystemManufacturer ?? string.Empty),
        ("Model", row => row.Stats?.SystemModel ?? string.Empty),
        ("Windows", row => row.Stats?.OsDisplay ?? string.Empty),
        ("Windows Utility", row => row.Stats?.AppVersion ?? string.Empty),
        ("Uptime (days)", row => row.Stats is null ? string.Empty : (row.Stats.UptimeSeconds / 86400.0).ToString("0.0", CultureInfo.InvariantCulture)),
        ("Processor", row => row.Stats?.CpuModel ?? string.Empty),
        ("CPU load %", row => Number(row.Stats?.CpuUsagePercent, "0")),
        ("CPU temp °C", row => row.Stats?.CpuTemperatureIsCpuSpecific == true ? Number(row.Stats.CpuTemperatureCelsius, "0") : string.Empty),
        ("RAM GB", row => row.Stats is { TotalMemoryBytes: > 0 } stats ? (stats.TotalMemoryBytes / 1024d / 1024 / 1024).ToString("0.0", CultureInfo.InvariantCulture) : string.Empty),
        ("RAM used %", row => Number(row.Stats?.RamUsagePercent, "0")),
        ("Drives", row => row.Stats is null ? string.Empty : string.Join("; ", row.Stats.Disks.Select(disk =>
            $"{disk.Name} {Gb(disk.FreeBytes)} free of {Gb(disk.TotalBytes)}"))),
        ("Lowest free %", row => row.Stats is { Disks.Count: > 0 } stats
            ? stats.Disks.Where(disk => disk.TotalBytes > 0).Select(disk => 100.0 * disk.FreeBytes / disk.TotalBytes).DefaultIfEmpty(100).Min().ToString("0", CultureInfo.InvariantCulture)
            : string.Empty),
        ("Battery %", row => row.Stats?.HasBattery == true ? Number(row.Stats.BatteryPercent, "0") : string.Empty),
        ("Battery health %", row => row.Stats?.HasBattery == true ? Number(row.Stats.BatteryHealthPercent, "0") : string.Empty),
        ("Battery design Wh", row => row.Stats?.HasBattery == true ? Number(row.Stats.BatteryDesignMWh / 1000, "0.0") : string.Empty),
        ("Battery now Wh", row => row.Stats?.HasBattery == true ? Number(row.Stats.BatteryFullMWh / 1000, "0.0") : string.Empty),
        ("Battery cycles", row => row.Stats?.HasBattery == true && row.Stats.BatteryCycleCount is uint cycles ? cycles.ToString(CultureInfo.InvariantCulture) : string.Empty),
        ("Last Windows update", row => row.Stats?.Security?.LastUpdateInstalledUtc?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty),
        ("Pending updates", row => row.Stats?.Security?.PendingUpdates?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
        ("Restart needed", row => row.Stats?.Security is { } security ? (security.RestartRequired ? "Yes" : "No") : string.Empty),
        ("Antivirus", row => row.Stats?.Security?.AntivirusName ?? string.Empty),
        ("Antivirus on", row => YesNo(row.Stats?.Security?.AntivirusOn)),
        ("Real-time protection", row => YesNo(row.Stats?.Security?.RealTimeProtectionOn)),
        ("Definitions (days old)", row => row.Stats?.Security?.DefinitionsUpdatedUtc is DateTime updated
            ? Math.Max(0, (DateTime.UtcNow - updated).TotalDays).ToString("0", CultureInfo.InvariantCulture)
            : string.Empty),
        ("Firewall on", row => YesNo(row.Stats?.Security?.FirewallOn)),
    };

    public static string BuildCsv(IEnumerable<RemotePcView> views)
    {
        StringBuilder csv = new();
        csv.AppendLine(string.Join(",", Columns.Select(column => Csv(column.Header))));
        foreach (Row row in Rows(views))
        {
            csv.AppendLine(string.Join(",", Columns.Select(column => Csv(column.Value(row)))));
        }

        return csv.ToString();
    }

    public static string BuildHtml(IEnumerable<RemotePcView> views, string generatedBy)
    {
        List<Row> rows = Rows(views).ToList();
        int online = rows.Count(row => row.Online);
        int atRisk = rows.Count(row => row.Stats?.Security is { } security
            && (security.AntivirusOn == false || security.RealTimeProtectionOn == false || security.FirewallOn == false));
        int lowDisk = rows.Count(row => row.Stats?.Disks.Any(disk => disk.TotalBytes > 0 && disk.FreeBytes < disk.TotalBytes * 0.10) == true);
        int wornBattery = rows.Count(row => row.Stats?.BatteryHealthPercent is < 60);
        int staleUpdates = rows.Count(row => row.Stats?.Security is { } security && RemotePcView.UpdatesStale(security));
        int outdatedApp = rows.Count(row => row.Stats is not null && Version.TryParse(row.Stats.AppVersion, out Version? version) && version < UpdateService.GetCurrentVersion());

        StringBuilder html = new();
        html.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>PC inventory report</title>
            <style>
              body { font-family: "Segoe UI", Arial, sans-serif; color: #1d1d1f; margin: 24px; }
              h1 { font-size: 22px; margin: 0 0 4px; } .muted { color: #6e6e73; font-size: 12px; }
              .summary { display: flex; flex-wrap: wrap; gap: 10px; margin: 16px 0; }
              .card { border: 1px solid #ddd; border-radius: 8px; padding: 8px 14px; min-width: 110px; }
              .card b { display: block; font-size: 20px; } .card span { font-size: 12px; color: #6e6e73; }
              .card.warn b { color: #c76a00; } .card.bad b { color: #d70015; }
              table { border-collapse: collapse; font-size: 11px; width: 100%; }
              th, td { border: 1px solid #ddd; padding: 4px 6px; text-align: left; vertical-align: top; }
              th { background: #f5f5f7; position: sticky; top: 0; }
              td.warn { background: #fff4e0; } td.bad { background: #ffe5e5; } tr.offline td { color: #8e8e93; }
              @media print { body { margin: 8mm; } th { position: static; } table { font-size: 8px; } @page { size: A4 landscape; } }
            </style></head><body>
            """);
        html.Append($"<h1>PC inventory report</h1><div class=\"muted\">{rows.Count} monitored PCs  •  created {DateTime.Now:d MMMM yyyy HH:mm} by {Encode(generatedBy)}  •  Windows Utility {UpdateService.GetCurrentVersion().ToString(3)}  •  To save as PDF, print this page and choose \"Microsoft Print to PDF\".</div>");
        html.Append("<div class=\"summary\">");
        AppendCard(html, online, $"online of {rows.Count}", string.Empty);
        AppendCard(html, atRisk, "at risk (antivirus or firewall off)", atRisk > 0 ? "bad" : string.Empty);
        AppendCard(html, staleUpdates, $"no Windows update in {RemotePcView.UpdatesStaleDays} days", staleUpdates > 0 ? "warn" : string.Empty);
        AppendCard(html, lowDisk, "with a drive under 10% free", lowDisk > 0 ? "warn" : string.Empty);
        AppendCard(html, wornBattery, "batteries under 60% health", wornBattery > 0 ? "warn" : string.Empty);
        AppendCard(html, outdatedApp, "with an older Windows Utility", outdatedApp > 0 ? "warn" : string.Empty);
        html.Append("</div><table><thead><tr>");
        foreach ((string header, _) in Columns)
        {
            html.Append($"<th>{Encode(header)}</th>");
        }

        html.Append("</tr></thead><tbody>");
        foreach (Row row in rows)
        {
            html.Append(row.Online ? "<tr>" : "<tr class=\"offline\">");
            foreach ((string header, Func<Row, string> value) in Columns)
            {
                string text = value(row);
                string level = Highlight(header, row);
                html.Append(level.Length > 0 ? $"<td class=\"{level}\">{Encode(text)}</td>" : $"<td>{Encode(text)}</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</tbody></table><p class=\"muted\">Offline PCs show the last readings received while this dashboard was running.</p></body></html>");
        return html.ToString();
    }

    private static IEnumerable<Row> Rows(IEnumerable<RemotePcView> views) =>
        views.Select(view => new Row(view, view.IsOnline ? view.Stats : view.LastKnownStats, view.IsOnline))
            .OrderBy(row => row.View.Title, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Cell colour for problems: red for protection off, amber for things to look at soon.</summary>
    private static string Highlight(string header, Row row)
    {
        RemoteStatsPayload? stats = row.Stats;
        RemoteSecurityInfo? security = stats?.Security;
        return header switch
        {
            "Status" when !row.Online => "warn",
            "Antivirus on" when security?.AntivirusOn == false => "bad",
            "Real-time protection" when security?.RealTimeProtectionOn == false => "bad",
            "Firewall on" when security?.FirewallOn == false => "bad",
            "Last Windows update" when security is not null && RemotePcView.UpdatesStale(security) => "warn",
            "Restart needed" when security?.RestartRequired == true => "warn",
            "Definitions (days old)" when security?.DefinitionsUpdatedUtc is DateTime updated && DateTime.UtcNow - updated > TimeSpan.FromDays(7) => "warn",
            "Lowest free %" when stats?.Disks.Any(disk => disk.TotalBytes > 0 && disk.FreeBytes < disk.TotalBytes * 0.10) == true => "warn",
            "Battery health %" when stats?.BatteryHealthPercent is < 60 => "warn",
            "CPU temp °C" when stats is { CpuTemperatureIsCpuSpecific: true, CpuTemperatureCelsius: double temperature } && temperature >= stats.CpuTemperatureAlertThresholdCelsius => "bad",
            "RAM used %" when stats?.RamUsagePercent >= 95 => "warn",
            "Windows Utility" when stats is not null && Version.TryParse(stats.AppVersion, out Version? version) && version < UpdateService.GetCurrentVersion() => "warn",
            _ => string.Empty
        };
    }

    private static void AppendCard(StringBuilder html, int value, string label, string level) =>
        html.Append($"<div class=\"card {level}\"><b>{value}</b><span>{Encode(label)}</span></div>");

    private static string Number(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(int? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string YesNo(bool? value) => value switch { true => "Yes", false => "No", _ => string.Empty };

    private static string Gb(long bytes) => RemotePcView.FormatGb(bytes);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    /// <summary>Quoted when needed; a leading =, +, - or @ is escaped so Excel never treats a value as a formula.</summary>
    private static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@".Contains(value[0]) && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }

        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
