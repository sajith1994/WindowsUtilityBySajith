using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.Dialogs;

/// <summary>
/// One monitored PC in detail: who is signed in, the last restart, the programs using the processor and memory now
/// (refreshed every 10 seconds while the window is open) and the crashes and problems Windows logged in 30 days.
/// </summary>
public partial class RemotePcDetailsWindow : Window
{
    private readonly RemoteMonitorClient _client;
    private readonly RemotePcEntry _pc;
    private readonly string _name;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _loading;

    private sealed record ProgramRow(string Program, string Cpu, string Memory);

    private sealed record ProblemRow(string When, string Type, string Summary, bool Serious);

    public RemotePcDetailsWindow(RemoteMonitorClient client, RemotePcEntry pc, string name)
    {
        InitializeComponent();
        _client = client;
        _pc = pc;
        _name = name;
        Title = $"Details: {name}";
        HeadingText.Text = name;
        SessionText.Text = "Connecting...";
        _timer.Tick += async (_, _) => await LoadAsync();
        Loaded += async (_, _) =>
        {
            await LoadAsync();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _lifetime.Cancel();
        };
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        RefreshButton.IsEnabled = false;
        try
        {
            RemotePcDetails details = await _client.GetDetailsAsync(_pc, _lifetime.Token);
            Show(details);
            StatusLine.Text = $"Updated {DateTime.Now:HH:mm:ss}  •  programs refresh every 10 seconds while this window is open";
            StatusLine.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "MutedTextBrush");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException)
        {
            StatusLine.Text = exception switch
            {
                TaskCanceledException => $"{_name} did not answer in time. It may be busy, asleep or offline.",
                HttpRequestException or IOException => $"Could not reach {_name}. It may be off, asleep, or sharing may be turned off on it.",
                _ => exception.Message
            };
            StatusLine.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
        }
        finally
        {
            _loading = false;
            if (!_lifetime.IsCancellationRequested)
            {
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private void Show(RemotePcDetails details)
    {
        TimeSpan uptime = DateTime.UtcNow - details.LastBootUtc;
        string users = details.Sessions.Count == 0
            ? "Nobody is signed in"
            : "Signed in: " + string.Join(", ", details.Sessions.Select(session => $"{session.User} ({session.State.ToLowerInvariant()})"));
        SessionText.Text = $"{users}  •  last restart {details.LastBootUtc.ToLocalTime():d MMM HH:mm} (up {FormatUptime(uptime)})";

        CpuGrid.ItemsSource = details.TopByCpu.Select(ToRow).ToList();
        MemoryGrid.ItemsSource = details.TopByMemory.Select(ToRow).ToList();

        string week = details.Last7Days is { HasAny: true } last7 ? last7.Describe() : "none";
        string month = details.Last30Days is { HasAny: true } last30 ? last30.Describe() : "none";
        ProblemSummaryText.Text = $"Last 7 days: {week}.  Last 30 days: {month}.";
        ProblemSummaryText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            details.Last7Days?.HasSerious == true ? "DangerBrush" : details.Last30Days?.HasAny == true ? "WarningBrush" : "MutedTextBrush");
        ProblemsGrid.ItemsSource = details.Problems.Select(problem => new ProblemRow(
            problem.TimeUtc.ToLocalTime().ToString("d MMM yyyy HH:mm"),
            KindLabel(problem.Kind),
            problem.Summary,
            problem.Kind is "BlueScreen" or "UnexpectedShutdown" or "DiskError" or "HardwareError")).ToList();
    }

    private static ProgramRow ToRow(RemoteProcessInfo program)
    {
        string name = string.IsNullOrWhiteSpace(program.Description) || program.Description.Equals(program.Name, StringComparison.OrdinalIgnoreCase)
            ? program.Name
            : $"{program.Description} ({program.Name})";
        if (program.Count > 1)
        {
            name += $"  ×{program.Count}";
        }

        string memory = program.MemoryBytes >= 1L << 30
            ? RemotePcView.FormatGb(program.MemoryBytes)
            : $"{program.MemoryBytes / 1024 / 1024:N0} MB";
        return new ProgramRow(name, $"{program.CpuPercent:0.0}%", memory);
    }

    private static string KindLabel(string kind) => kind switch
    {
        "BlueScreen" => "Blue screen",
        "UnexpectedShutdown" => "Unexpected shutdown",
        "AppCrash" => "App crash",
        "AppFreeze" => "App froze",
        "DiskError" => "Disk error",
        "HardwareError" => "Hardware error",
        _ => kind
    };

    private static string FormatUptime(TimeSpan uptime) => uptime.TotalDays >= 1
        ? $"{(int)uptime.TotalDays} d {uptime.Hours} h"
        : uptime.TotalHours >= 1 ? $"{uptime.Hours} h {uptime.Minutes} m" : $"{Math.Max(1, uptime.Minutes)} m";

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
