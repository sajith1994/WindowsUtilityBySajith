using System.Net;
using System.Windows;
using WindowsUtility.Services;

namespace WindowsUtility.Dialogs;

/// <summary>Toner and ink levels of the network printers this PC knows (installed or found by the network scan).</summary>
public partial class PrinterSuppliesWindow : Window
{
    private readonly List<(string Name, string Address)> _printers;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _loading;

    public sealed record SupplyRow(string Name, double Percent, string Text, SupplyState State);

    public sealed record PrinterRow(string Name, string Detail, IReadOnlyList<SupplyRow> Supplies, string Message);

    public PrinterSuppliesWindow(IEnumerable<(string Name, string Address)> printers)
    {
        InitializeComponent();
        _printers = printers
            .Where(printer => !string.IsNullOrWhiteSpace(printer.Address))
            .GroupBy(printer => printer.Address, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _lifetime.Cancel();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void CheckAddressButton_Click(object sender, RoutedEventArgs e)
    {
        string address = AddressBox.Text.Trim();
        if (!IPAddress.TryParse(address, out IPAddress? parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            StatusLine.Text = "Enter the printer's IPv4 address, for example 192.168.1.30.";
            StatusLine.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
            return;
        }

        if (_printers.All(printer => printer.Address != address))
        {
            _printers.Add((address, address));
        }

        AddressBox.Clear();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        EmptyText.Visibility = _printers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_printers.Count == 0)
        {
            StatusLine.Text = string.Empty;
            return;
        }

        _loading = true;
        RefreshButton.IsEnabled = false;
        StatusLine.Text = $"Asking {_printers.Count} printer{(_printers.Count == 1 ? "" : "s")} for their levels...";
        StatusLine.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "MutedTextBrush");
        try
        {
            PrinterSuppliesResult[] results = await Task.WhenAll(_printers.Select(printer => ReadAsync(printer.Address, _lifetime.Token)));
            PrintersList.ItemsSource = _printers.Zip(results, (printer, result) => ToRow(printer.Name, result))
                .OrderByDescending(row => row.Supplies.Any(supply => supply.State is SupplyState.Low or SupplyState.Empty))
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            int low = results.Count(result => result.NeedsAttention);
            StatusLine.Text = low > 0
                ? $"{low} printer{(low == 1 ? " needs" : "s need")} new ink or toner soon. Checked {DateTime.Now:HH:mm}."
                : $"Checked {DateTime.Now:HH:mm}.";
            StatusLine.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, low > 0 ? "WarningBrush" : "MutedTextBrush");
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

    /// <summary>Printers installed by name are looked up first; SNMP needs the address.</summary>
    private static async Task<PrinterSuppliesResult> ReadAsync(string address, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(address, out _))
        {
            try
            {
                IPAddress[] found = await Dns.GetHostAddressesAsync(address, System.Net.Sockets.AddressFamily.InterNetwork, cancellationToken);
                if (found.Length == 0)
                {
                    return new PrinterSuppliesResult(address, string.Empty, Array.Empty<PrinterSupply>(), $"{address} could not be found on the network.");
                }

                address = found[0].ToString();
            }
            catch (System.Net.Sockets.SocketException)
            {
                return new PrinterSuppliesResult(address, string.Empty, Array.Empty<PrinterSupply>(), $"{address} could not be found on the network.");
            }
        }

        return await PrinterSuppliesService.ReadAsync(address, cancellationToken);
    }

    private static PrinterRow ToRow(string name, PrinterSuppliesResult result)
    {
        List<SupplyRow> supplies = result.Supplies.Select(supply => new SupplyRow(
                supply.IsWaste ? $"{supply.Name} (fills up)" : supply.Name,
                supply.Percent ?? (supply.State == SupplyState.Ok ? 100 : 0),
                PrinterSuppliesService.Describe(supply),
                supply.State))
            .ToList();
        string detail = result.Address;
        if (result.Source.Length > 0)
        {
            detail += $"  •  read over {result.Source}";
        }

        return new PrinterRow(name, detail, supplies, result.Error ?? string.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
