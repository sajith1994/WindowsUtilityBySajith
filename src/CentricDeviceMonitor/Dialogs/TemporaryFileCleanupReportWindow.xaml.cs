using System.Diagnostics;
using System.Windows;
using CentricDeviceMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor.Dialogs;

public partial class TemporaryFileCleanupReportWindow : Window
{
    private readonly TemporaryFileCleanupResult _result;

    public TemporaryFileCleanupReportWindow(TemporaryFileCleanupResult result)
    {
        _result = result;
        InitializeComponent();

        SpaceReclaimedText.Text = result.SizeDisplay;
        FreeSpaceChangeText.Text = result.ObservedFreeSpaceIncreaseDisplay;
        FilesDeletedText.Text = result.FilesDeleted.ToString("N0");
        FoldersDeletedText.Text = result.DirectoriesDeleted.ToString("N0");
        SkippedText.Text = result.ItemsSkipped.ToString("N0");
        ItemsGrid.ItemsSource = result.Items;
        SummaryText.Text = $"{result.Items.Count:N0} item record(s). Detailed history: {result.LogFilePath}";
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_result.LogFilePath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(exception.Message, "Cleanup log", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
