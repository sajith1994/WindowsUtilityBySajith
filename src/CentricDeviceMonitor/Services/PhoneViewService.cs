using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// The dashboard's half of the phone page: after each check of the monitored PCs it writes what the tiles show to a
/// file the background service serves to phones that have the link (the service owns the network port).
/// </summary>
public static class PhoneViewService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // No 0/O or 1/I, so a link typed from the screen into a phone is not mistyped.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>A new 12-character link key (60 bits).</summary>
    public static string NewKey() =>
        new(Enumerable.Range(0, 12).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());

    public static string Link(string address, int port, string key) =>
        $"http://{address}{(port == 80 ? string.Empty : $":{port}")}{RemoteMonitoringProtocol.PhonePagePath}?k={key}";

    public static PhoneViewData Build(IEnumerable<RemotePcView> views, string mainPcName, DateTime nowUtc) => new()
    {
        MainPcName = mainPcName,
        UpdatedUtc = nowUtc,
        Pcs = views.Select(view => new PhoneViewPc
        {
            Name = view.Title,
            Group = view.Group,
            StatusKind = view.StatusKind,
            StatusLabel = view.StatusLabel,
            Message = view.HasStats ? string.Empty : view.Message,
            CpuPercent = view.Stats?.CpuUsagePercent ?? (view.HasStats ? 0 : null),
            Temperature = view.HasStats && view.Stats?.CpuTemperatureCelsius is not null ? view.TemperatureText : string.Empty,
            RamPercent = view.Stats?.RamUsagePercent,
            Disks = view.Disks.Select(disk => new PhoneViewDisk { Name = disk.Name, UsedPercent = disk.UsedPercent, Free = disk.Detail.Split(" free")[0], Low = disk.IsLow }).ToList(),
            SignedIn = view.HasStats ? view.SessionsText : string.Empty,
            Notes = DailySummaryService.Issues(view).Where(_ => view.IsOnline).Select(issue => issue.Text).ToList(),
            LastSeen = view.IsOnline ? string.Empty : view.LastSeen is DateTime seen ? $"Last seen {seen:ddd HH:mm}" : string.Empty
        }).ToList()
    };

    /// <summary>Writes the phone page data; errors only go to the log (the page then shows the last data and its age).</summary>
    public static void Write(PhoneViewData data, string? path = null)
    {
        path ??= SharedDataPaths.PhoneViewDataFilePath;
        try
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ApplicationLogService.WriteException("Phone page data", exception);
        }
    }

    /// <summary>Removes the data when the phone page is turned off, so nothing is left to serve.</summary>
    public static void Delete(string? path = null)
    {
        try
        {
            File.Delete(path ?? SharedDataPaths.PhoneViewDataFilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
