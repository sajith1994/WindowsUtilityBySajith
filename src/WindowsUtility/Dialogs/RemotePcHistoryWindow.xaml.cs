using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using WindowsUtility.Controls;
using WindowsUtility.Models;
using WpfColor = System.Windows.Media.Color;

namespace WindowsUtility.Dialogs;

/// <summary>
/// A monitored PC's last 24 hours (5-minute points) or 7 days (30-minute points) of CPU load, temperature, memory,
/// disk activity and network traffic, recorded every minute by that PC's own background service. Gaps are times the
/// PC was off or asleep.
/// </summary>
public partial class RemotePcHistoryWindow : Window
{
    private readonly Func<int, CancellationToken, Task<RemoteHistory>> _load;
    private readonly string _name;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LiveChartSeries _cpu = new("CPU load", "%", WpfColor.FromRgb(0x3B, 0x82, 0xF6), 0, 100);
    private readonly LiveChartSeries _temperature = new("Temperature (highest)", "°C", WpfColor.FromRgb(0xEF, 0x44, 0x44));
    private readonly LiveChartSeries _ram = new("Memory", "%", WpfColor.FromRgb(0x8B, 0x5C, 0xF6), 0, 100);
    private readonly LiveChartSeries _disk = new("Disk activity (busiest disk)", "%", WpfColor.FromRgb(0x14, 0xB8, 0xA6), 0, 100);
    private readonly LiveChartSeries _download = new("Network download", "Mbps", WpfColor.FromRgb(0x22, 0xC5, 0x5E));
    private readonly LiveChartSeries _upload = new("Network upload", "Mbps", WpfColor.FromRgb(0xF5, 0x9E, 0x0B));
    private RemoteHistory? _history;
    private bool _loading;

    /// <param name="load">Reads the history for 1 or 7 days (from the monitored PC).</param>
    /// <param name="temperatureLimit">That PC's alert temperature, drawn as a dashed line.</param>
    public RemotePcHistoryWindow(string name, Func<int, CancellationToken, Task<RemoteHistory>> load, double? temperatureLimit = null)
    {
        InitializeComponent();
        _name = name;
        _load = load;
        Title = $"History: {name}";
        HeadingText.Text = $"History: {name}";
        SummaryText.Text = "Loading...";
        _temperature.Threshold = temperatureLimit;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }

    private int Days => RangeComboBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int days) ? days : 1;

    private async void RangeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            await LoadAsync();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void SeriesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            Plot();
        }
    }

    private async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        RefreshButton.IsEnabled = false;
        RangeComboBox.IsEnabled = false;
        int days = Days;
        try
        {
            _history = await _load(days, _lifetime.Token);
            Plot();
            SummaryText.Text = Summarize(_history);
            StatusLine.Text = $"Updated {DateTime.Now:HH:mm}  •  recorded every minute by {_name}'s background service and kept for 8 days. Hover to read a value; click to pin it.";
            StatusLine.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException)
        {
            SummaryText.Text = string.Empty;
            Chart.EmptyText = "No history to show.";
            Chart.SetData(Array.Empty<DateTime>(), Array.Empty<LiveChartSeries>(), TimeSpan.FromDays(days));
            StatusLine.Text = exception switch
            {
                TaskCanceledException => $"{_name} did not answer in time. It may be busy, asleep or offline.",
                HttpRequestException or IOException => $"Could not reach {_name}. It may be off, asleep, or sharing may be turned off on it.",
                _ => exception.Message
            };
            StatusLine.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        }
        finally
        {
            _loading = false;
            if (!_lifetime.IsCancellationRequested)
            {
                RefreshButton.IsEnabled = true;
                RangeComboBox.IsEnabled = true;
            }
        }
    }

    private void Plot()
    {
        if (_history is not { } history)
        {
            return;
        }

        List<DateTime> times = history.Points.Select(point => point.TimeUtc.ToLocalTime()).ToList();
        void Fill(LiveChartSeries series, Func<RemoteHistoryPoint, double?> value, System.Windows.Controls.CheckBox check)
        {
            series.Values.Clear();
            series.Values.AddRange(history.Points.Select(value));
            series.IsVisible = check.IsChecked == true;
        }

        Fill(_cpu, point => point.CpuPercent, CpuCheck);
        Fill(_temperature, point => point.TemperatureCelsius, TemperatureCheck);
        Fill(_ram, point => point.RamPercent, RamCheck);
        Fill(_disk, point => point.DiskActivePercent, DiskCheck);
        Fill(_download, point => point.ReceiveBitsPerSec / 1_000_000, DownloadCheck);
        Fill(_upload, point => point.SendBitsPerSec / 1_000_000, UploadCheck);
        Chart.EmptyText = history.Points.Count == 0
            ? $"No history yet. {_name} records a reading every minute from when it has Windows Utility 2.0.77 or later."
            : "Select a reading above to plot it.";
        Chart.SetData(times, new[] { _cpu, _temperature, _ram, _disk, _download, _upload }, TimeSpan.FromDays(history.Days));
    }

    /// <summary>"Average CPU 12% (peak 88% Tue 14:05) • hottest 92 °C Mon 21:30 • memory up to 81% • no readings for 9 h (off or asleep)".</summary>
    internal static string Summarize(RemoteHistory history)
    {
        List<RemoteHistoryPoint> recorded = history.Points.Where(point => point.CpuPercent is not null || point.RamPercent is not null).ToList();
        if (recorded.Count == 0)
        {
            return "No readings recorded in this period.";
        }

        static string When(RemoteHistoryPoint point) => point.TimeUtc.ToLocalTime().ToString("ddd HH:mm");
        List<string> parts = new();
        if (recorded.Where(point => point.CpuPercent is not null).ToList() is { Count: > 0 } cpu)
        {
            RemoteHistoryPoint peak = cpu.MaxBy(point => point.CpuPercent)!;
            parts.Add($"average CPU {cpu.Average(point => point.CpuPercent!.Value):0}% (peak {peak.CpuPercent:0}% {When(peak)})");
        }

        if (recorded.Where(point => point.TemperatureCelsius is not null).MaxBy(point => point.TemperatureCelsius) is { } hottest)
        {
            parts.Add($"hottest {hottest.TemperatureCelsius:0} °C {When(hottest)}");
        }

        if (recorded.Where(point => point.RamPercent is not null).MaxBy(point => point.RamPercent) is { } memory)
        {
            parts.Add($"memory up to {memory.RamPercent:0}%");
        }

        int gaps = history.Points.Count - recorded.Count;
        if (gaps > 0)
        {
            TimeSpan missing = TimeSpan.FromMinutes(gaps * history.StepMinutes);
            parts.Add($"no readings for {(missing.TotalHours >= 1 ? $"{missing.TotalHours:0.#} h" : $"{missing.TotalMinutes:0} min")} (off or asleep)");
        }

        string text = string.Join("  •  ", parts);
        text = char.ToUpperInvariant(text[0]) + text[1..];
        if (history.RecordingSinceUtc is DateTime since && since > DateTime.UtcNow.AddDays(-history.Days))
        {
            text += $".  Recording since {since.ToLocalTime():ddd d MMM HH:mm}.";
        }

        return text;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
