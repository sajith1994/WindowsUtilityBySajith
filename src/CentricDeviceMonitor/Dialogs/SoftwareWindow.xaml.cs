using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor.Dialogs;

/// <summary>A PC's installed programs, or the result of reading several PCs at once.</summary>
public sealed record SoftwareSource(string Pc, RemoteSoftwareList? List, string? Error);

/// <summary>
/// Installed programs of one monitored PC, or of every monitored PC at once with a PC column, so a search answers
/// "which PCs have AnyDesk?" or "who still has an old Chrome?". The rows shown can be saved as CSV.
/// </summary>
public partial class SoftwareWindow : Window
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<SoftwareSource>>> _load;
    private readonly bool _severalPcs;
    private readonly CancellationTokenSource _lifetime = new();
    private List<ProgramRow> _rows = new();
    private bool _loading;

    public sealed record ProgramRow(string Pc, string Name, string Version, string Publisher, string Installed, DateTime? InstalledSort,
        string Size, long SizeSort, string InstalledFor);

    /// <param name="title">"Software: OFFICE-PC" or "Software on all monitored PCs".</param>
    /// <param name="severalPcs">Shows the PC column.</param>
    public SoftwareWindow(string title, bool severalPcs, Func<CancellationToken, Task<IReadOnlyList<SoftwareSource>>> load)
    {
        InitializeComponent();
        _load = load;
        _severalPcs = severalPcs;
        Title = title;
        HeadingText.Text = title;
        PcColumn.Visibility = severalPcs ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await LoadAsync();
        };
        Closed += (_, _) => _lifetime.Cancel();
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
        StatusLine.Text = _severalPcs ? "Reading the programs on every PC that is online..." : "Reading the programs...";
        StatusLine.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        try
        {
            IReadOnlyList<SoftwareSource> sources = await _load(_lifetime.Token);
            _rows = sources.Where(source => source.List is not null)
                .SelectMany(source => source.List!.Programs.Select(program => ToRow(source.Pc, program)))
                .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Pc, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            ApplySearch();

            List<SoftwareSource> failed = sources.Where(source => source.List is null).ToList();
            int read = sources.Count - failed.Count;
            StatusLine.Text = failed.Count == 0
                ? _severalPcs ? $"Read from {read} PC{(read == 1 ? "" : "s")} at {DateTime.Now:HH:mm}." : $"Read at {DateTime.Now:HH:mm}."
                : (_severalPcs ? $"Read from {read} of {sources.Count} PCs. Not read: " : string.Empty)
                  + string.Join("; ", failed.Select(source => _severalPcs ? $"{source.Pc} ({source.Error})" : source.Error));
            StatusLine.SetResourceReference(TextBlock.ForegroundProperty, failed.Count == 0 ? "MutedTextBrush" : read == 0 ? "DangerBrush" : "WarningBrush");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
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

    private static ProgramRow ToRow(string pc, RemoteProgram program) => new(
        pc,
        program.Name,
        program.Version,
        program.Publisher,
        program.InstalledOn?.ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? string.Empty,
        program.InstalledOn,
        program.SizeBytes is long bytes ? bytes >= 1L << 30 ? RemotePcView.FormatGb(bytes) : $"{Math.Max(1, bytes / 1024 / 1024):N0} MB" : string.Empty,
        program.SizeBytes ?? 0,
        program.InstalledFor);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplySearch();
    }

    private void ApplySearch()
    {
        string[] words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<ProgramRow> shown = _rows.Where(row => words.All(word =>
                row.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                || row.Publisher.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                || row.Version.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                || row.Pc.Contains(word, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();
        ProgramsGrid.ItemsSource = shown;
        int pcs = shown.Select(row => row.Pc).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        CountText.Text = words.Length == 0
            ? $"{shown.Count:N0} programs"
            : $"{shown.Count:N0} of {_rows.Count:N0}{(_severalPcs ? $" on {pcs} PC{(pcs == 1 ? "" : "s")}" : string.Empty)}";
        ExportButton.IsEnabled = shown.Count > 0;
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProgramsGrid.ItemsSource is not List<ProgramRow> shown || shown.Count == 0)
        {
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = "Export installed software",
            FileName = $"Installed software {DateTime.Now:yyyy-MM-dd}",
            Filter = "Spreadsheet for Excel (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            StringBuilder csv = new();
            csv.AppendLine("PC,Program,Version,Publisher,Installed on,Size MB,Installed for");
            foreach (ProgramRow row in shown)
            {
                csv.AppendLine(string.Join(",", new[]
                {
                    row.Pc, row.Name, row.Version, row.Publisher,
                    row.InstalledSort?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                    row.SizeSort > 0 ? (row.SizeSort / 1024d / 1024).ToString("0", CultureInfo.InvariantCulture) : string.Empty,
                    row.InstalledFor
                }.Select(InventoryReportService.CsvField)));
            }

            File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
            StatusLine.Text = $"Saved {shown.Count:N0} rows to {dialog.FileName}.";
            ApplicationLogService.WriteMessage("Network PCs", $"Software list saved to {dialog.FileName}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show(this, $"The file could not be saved: {exception.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Reads one PC's programs for the window, turning connection problems into a short reason.</summary>
    public static async Task<SoftwareSource> ReadAsync(RemoteMonitorClient client, RemotePcEntry pc, string name, CancellationToken cancellationToken)
    {
        try
        {
            return new SoftwareSource(name, await client.GetSoftwareAsync(pc, cancellationToken), null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException
            && !cancellationToken.IsCancellationRequested)
        {
            return new SoftwareSource(name, null, exception switch
            {
                TaskCanceledException => "no answer in time",
                HttpRequestException or IOException => "offline or not reachable",
                _ => exception.Message
            });
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
