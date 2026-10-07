using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WpfMessageBox = System.Windows.MessageBox;
using System.Windows.Threading;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.Dialogs;

public partial class BatteryDiagnosticsWindow : Window
{
    private readonly BatteryDiagnosticsService _batteryDiagnosticsService = new();
    private readonly DispatcherTimer _refreshTimer;
    private bool _refreshing;

    public BatteryDiagnosticsWindow()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshBatteryAsync(showErrors: false);

        Loaded += async (_, _) =>
        {
            await RefreshBatteryAsync(showErrors: true);
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshBatteryAsync(showErrors: true);
    }

    private async Task RefreshBatteryAsync(bool showErrors)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Reading Windows battery telemetry...";

        try
        {
            IReadOnlyList<BatteryHealthInfo> batteries = await _batteryDiagnosticsService.GetBatteryHealthAsync();
            BatteryHealthInfo? selectedBeforeRefresh = BatteryGrid.SelectedItem as BatteryHealthInfo;
            string selectedInstance = selectedBeforeRefresh?.InstanceName ?? string.Empty;

            BatteryGrid.ItemsSource = batteries;
            if (batteries.Count == 0)
            {
                StatusText.Text = "No laptop battery detected.";
                ClearSummary();
                GenerateReportButton.IsEnabled = false;
                return;
            }

            GenerateReportButton.IsEnabled = true;
            StatusText.Text = batteries.Count == 1 ? "1 battery detected." : $"{batteries.Count} batteries detected.";

            BatteryHealthInfo selected = batteries.FirstOrDefault(item =>
                string.Equals(item.InstanceName, selectedInstance, StringComparison.OrdinalIgnoreCase)) ?? batteries[0];
            BatteryGrid.SelectedItem = selected;
            BatteryGrid.ScrollIntoView(selected);
            UpdateSummary(selected);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Battery diagnostics window", exception);
            StatusText.Text = "Battery information could not be read.";
            if (showErrors)
            {
                WpfMessageBox.Show(this, exception.Message, "Battery diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _refreshing = false;
        }
    }

    private void BatteryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BatteryGrid.SelectedItem is BatteryHealthInfo battery)
        {
            UpdateSummary(battery);
        }
    }

    private void UpdateSummary(BatteryHealthInfo battery)
    {
        ChargeRemainingText.Text = battery.ChargeRemainingText;
        CurrentCapacityText.Text = $"Current capacity: {battery.RemainingCapacityText}";
        BatteryHealthText.Text = battery.HealthText;
        CapacityComparisonText.Text = $"Full {battery.FullChargeCapacityText} / design {battery.DesignedCapacityText}";
        RemainingRuntimeText.Text = battery.RemainingRuntimeText;
        BatteryStateText.Text = $"{battery.StateText} · {battery.CycleCountText} cycle(s)";
        BatteryModelText.Text = battery.Model;
        BatteryManufacturerText.Text = $"{battery.Manufacturer} · {battery.Chemistry}";
    }

    private void ClearSummary()
    {
        ChargeRemainingText.Text = "--";
        CurrentCapacityText.Text = "Current capacity: --";
        BatteryHealthText.Text = "--";
        CapacityComparisonText.Text = "Full charge / design: --";
        RemainingRuntimeText.Text = "--";
        BatteryStateText.Text = "State: --";
        BatteryModelText.Text = "No battery detected";
        BatteryManufacturerText.Text = "This device may be a desktop or Windows did not expose battery telemetry.";
    }

    private async void GenerateReportButton_Click(object sender, RoutedEventArgs e)
    {
        GenerateReportButton.IsEnabled = false;
        StatusText.Text = "Generating Windows battery report...";
        try
        {
            string path = await _batteryDiagnosticsService.GenerateWindowsBatteryReportAsync();
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            StatusText.Text = "Windows battery report generated.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Generate Windows battery report", exception);
            StatusText.Text = "Battery report generation failed.";
            WpfMessageBox.Show(this, exception.Message, "Windows battery report", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            GenerateReportButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
