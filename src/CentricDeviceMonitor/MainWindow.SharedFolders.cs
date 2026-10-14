using System.Windows;
using CentricDeviceMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor;

/// <summary>Network Diagnostics: folders shared from this PC and shares on the local network.</summary>
public partial class MainWindow
{
    private readonly SharedFoldersService _sharedFoldersService = new();
    private bool _scanningShares;

    private async void RefreshLocalSharesButton_Click(object sender, RoutedEventArgs e) => await RefreshLocalSharesAsync();

    private async Task RefreshLocalSharesAsync()
    {
        try
        {
            IReadOnlyList<LocalShareInfo> shares = await _sharedFoldersService.GetLocalSharesAsync();
            LocalSharesList.ItemsSource = shares;
            LocalSharesHeaderText.Text = $"Shared from this PC ({shares.Count})";
            LocalSharesEmptyText.Text = "This PC is not sharing any folders. (Hidden administrative shares such as C$ are not listed.)";
            LocalSharesEmptyText.Visibility = shares.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Local shares", exception);
            LocalSharesEmptyText.Text = $"Could not read shared folders: {exception.Message}";
            LocalSharesEmptyText.Visibility = Visibility.Visible;
        }
    }

    private async void ScanNetworkSharesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scanningShares)
        {
            return;
        }

        _scanningShares = true;
        ScanNetworkSharesButton.IsEnabled = false;
        ScanNetworkSharesButton.Content = "Scanning...";
        NetworkSharesEmptyText.Text = "Looking for PCs with file sharing on the local network...";
        NetworkSharesEmptyText.Visibility = Visibility.Visible;
        try
        {
            IReadOnlyList<NetworkShareInfo> shares = await _sharedFoldersService.ScanNetworkSharesAsync();
            NetworkSharesList.ItemsSource = shares;
            int pcs = shares.Select(share => share.Address).Distinct().Count();
            NetworkSharesHeaderText.Text = $"Shared on the network ({shares.Count(share => !share.NeedsSignIn)} shares on {pcs} PCs)";
            NetworkSharesEmptyText.Text = $"No PCs with file sharing answered on this subnet. Last scan {DateTime.Now:t}.";
            NetworkSharesEmptyText.Visibility = shares.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"Network share scan finished: {shares.Count(share => !share.NeedsSignIn)} shares on {pcs} PCs.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Network share scan", exception);
            NetworkSharesEmptyText.Text = $"Network scan failed: {exception.Message}";
            NetworkSharesEmptyText.Visibility = Visibility.Visible;
        }
        finally
        {
            ScanNetworkSharesButton.Content = "Scan network";
            ScanNetworkSharesButton.IsEnabled = true;
            _scanningShares = false;
        }
    }

    private void OpenShareButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            // Explorer opens in the user's normal session even though this dashboard runs elevated.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(this, exception.Message, "Open shared folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddShareToExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NetworkShareInfo share })
        {
            return;
        }

        try
        {
            string location = SharedFoldersService.AddToFileExplorer(share.UncPath, share.LocationName);
            ApplicationLogService.WriteMessage("Shared folders", $"Added {share.UncPath} to File Explorer at {location}.");
            StatusText.Text = $"{share.UncPath} added to File Explorer under This PC > Network locations.";
            if (WpfMessageBox.Show(this,
                    $"{share.UncPath} now appears in File Explorer under This PC > Network locations.\n\nOpen This PC now?",
                    "Add to File Explorer", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:MyComputerFolder") { UseShellExecute = true });
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Add share to File Explorer", exception);
            WpfMessageBox.Show(this, exception.Message, "Add to File Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
