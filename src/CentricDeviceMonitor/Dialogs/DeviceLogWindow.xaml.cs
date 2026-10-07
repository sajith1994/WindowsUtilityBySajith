using System.Collections.ObjectModel;
using System.Windows;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor.Dialogs;

public partial class DeviceLogWindow : Window
{
    private readonly DeviceEntry _device;
    private readonly DeviceLogService _logService;

    public ObservableCollection<DevicePingLogEntry> Entries { get; } = new();

    public DeviceLogWindow(DeviceEntry device, DeviceLogService logService)
    {
        InitializeComponent();
        _device = device;
        _logService = logService;
        DataContext = this;

        DeviceNameText.Text = $"Connection & restart history - {_device.Name}";
        DeviceIpText.Text = _device.IpAddress;
        Loaded += DeviceLogWindow_Loaded;
    }

    private async void DeviceLogWindow_Loaded(object sender, RoutedEventArgs e) => await LoadEntriesAsync();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadEntriesAsync();

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = WpfMessageBox.Show(
            $"Clear the complete connection and restart history for {_device.Name}?",
            "Clear device history",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _logService.ClearAsync(_device.Id);
            Entries.Clear();
            UpdateSummary();
            LogStatusText.Text = "The device connection and restart history was cleared.";
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(exception.Message, "Device history", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task LoadEntriesAsync()
    {
        try
        {
            LogStatusText.Text = "Loading device connection and restart history...";
            IReadOnlyList<DevicePingLogEntry> loadedEntries = await _logService.LoadAsync(_device.Id);

            Entries.Clear();
            foreach (DevicePingLogEntry entry in loadedEntries)
            {
                Entries.Add(entry);
            }

            UpdateSummary();
            LogStatusText.Text = Entries.Count == 0
                ? "No connection-loss or restart/shutdown reachability events have been recorded. Normal successful pings are not stored."
                : "Newest device events are shown first. Shutdown/restart labels are reachability-based unless remote boot telemetry is available.";
        }
        catch (Exception exception)
        {
            LogStatusText.Text = "The device history could not be loaded.";
            WpfMessageBox.Show(exception.Message, "Device history", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateSummary()
    {
        EntryCountText.Text = Entries.Count.ToString();
        ActiveCountText.Text = Entries.Count(entry => entry.IsActive).ToString();
    }
}
