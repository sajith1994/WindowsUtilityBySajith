using System.Collections.ObjectModel;
using System.Windows;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor.Dialogs;

public partial class CpuTemperatureLogWindow : Window
{
    private readonly CpuTemperatureLogService _eventLogService;
    private readonly CpuTemperatureSampleLogService _sampleLogService = new();
    private readonly ObservableCollection<CpuTemperatureLogEntry> _eventEntries = new();
    private readonly ObservableCollection<CpuTemperatureSampleEntry> _sampleEntries = new();

    public CpuTemperatureLogWindow(CpuTemperatureLogService logService)
    {
        InitializeComponent();
        _eventLogService = logService;
        TemperatureLogGrid.ItemsSource = _eventEntries;
        TemperatureSampleGrid.ItemsSource = _sampleEntries;
        Loaded += async (_, _) => await LoadEntriesAsync();
    }

    private async Task LoadEntriesAsync()
    {
        IReadOnlyList<CpuTemperatureLogEntry> savedEvents = await _eventLogService.LoadAsync();
        IReadOnlyList<CpuTemperatureSampleEntry> savedSamples = await _sampleLogService.LoadLatestAsync(2000);

        _eventEntries.Clear();
        foreach (CpuTemperatureLogEntry entry in savedEvents)
        {
            _eventEntries.Add(entry);
        }

        _sampleEntries.Clear();
        foreach (CpuTemperatureSampleEntry entry in savedSamples)
        {
            _sampleEntries.Add(entry);
        }

        int activeCount = _eventEntries.Count(entry => entry.IsActive);
        string lastSample = _sampleEntries.FirstOrDefault()?.RecordedAtDisplay ?? "none yet";
        SummaryText.Text = $"{_sampleEntries.Count} recent sample(s), last sample {lastSample}; {_eventEntries.Count} overheat event(s), {activeCount} active";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadEntriesAsync();

    private async void ClearSamplesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sampleEntries.Count == 0)
        {
            WpfMessageBox.Show(
                "There are no CPU temperature samples to clear.",
                "CPU temperature log",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult answer = WpfMessageBox.Show(
            "Clear all saved CPU temperature samples? Overheat incident records will not be removed.",
            "Clear temperature samples",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await _sampleLogService.ClearAsync();
        await LoadEntriesAsync();
    }

    private async void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_eventEntries.Any(entry => !entry.IsActive))
        {
            WpfMessageBox.Show(
                "There are no completed CPU temperature events to clear.",
                "CPU temperature log",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult answer = WpfMessageBox.Show(
            "Clear all completed CPU temperature events? Any active event will be kept.",
            "Clear completed events",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await _eventLogService.ClearCompletedAsync();
        await LoadEntriesAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
