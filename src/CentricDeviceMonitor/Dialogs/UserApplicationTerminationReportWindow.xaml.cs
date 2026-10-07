using System.Diagnostics;
using System.Windows;
using CentricDeviceMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor.Dialogs;

public partial class UserApplicationTerminationReportWindow : Window
{
    private readonly UserApplicationTerminationResult _result;

    public UserApplicationTerminationReportWindow(UserApplicationTerminationResult result)
    {
        _result = result;
        InitializeComponent();

        EndedText.Text = result.ApplicationsEnded.ToString("N0");
        ClosedNormallyText.Text = result.ClosedNormally.ToString("N0");
        ForceTerminatedText.Text = result.ForceTerminated.ToString("N0");
        FailedText.Text = result.Failed.ToString("N0");
        SystemExcludedText.Text = result.SystemProcessesExcluded.ToString("N0");
        ApplicationsGrid.ItemsSource = result.Entries;
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
            WpfMessageBox.Show(exception.Message, "Application termination log", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
