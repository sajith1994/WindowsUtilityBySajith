using System.Collections.ObjectModel;
using System.Windows;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor.Dialogs;

public partial class SystemHealthLogWindow : Window
{
    private readonly SystemHealthLogService _logService;
    private readonly ObservableCollection<SystemHealthLogEntry> _entries = new();

    public SystemHealthLogWindow(SystemHealthLogService logService)
    {
        InitializeComponent();
        _logService = logService;
        SystemHealthLogGrid.ItemsSource = _entries;
        Loaded += async (_, _) => await LoadEntriesAsync();
    }

    private async Task LoadEntriesAsync()
    {
        IReadOnlyList<SystemHealthLogEntry> savedEntries = await _logService.LoadAsync();
        _entries.Clear();
        foreach (SystemHealthLogEntry entry in savedEntries)
        {
            _entries.Add(entry);
        }

        int activeCount = _entries.Count(entry => entry.IsActive);
        SummaryText.Text = $"{_entries.Count} event(s), {activeCount} currently active";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadEntriesAsync();

    private async void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_entries.Any(entry => !entry.IsActive))
        {
            WpfMessageBox.Show(
                "There are no completed system health events to clear.",
                "System health log",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult answer = WpfMessageBox.Show(
            "Clear all completed system health events? Any active event will be kept.",
            "Clear completed events",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await _logService.ClearCompletedAsync();
        await LoadEntriesAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
