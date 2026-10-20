using System.Windows;
using CentricDeviceMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor;

/// <summary>Network Center: folders shared from this PC and shares on the local network.</summary>
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

    private async void RefreshAddedFoldersButton_Click(object sender, RoutedEventArgs e) => await RefreshAddedFoldersAsync(testAll: true);

    /// <summary>Lists the network locations and mapped drives added to this PC, then tests them all.</summary>
    private async Task RefreshAddedFoldersAsync(bool testAll)
    {
        try
        {
            IReadOnlyList<AddedNetworkFolder> folders = await _sharedFoldersService.GetAddedNetworkFoldersAsync();
            AddedFoldersList.ItemsSource = folders;
            int drives = folders.Count(folder => folder.Kind == AddedFolderKind.MappedDrive);
            AddedFoldersHeaderText.Text = $"Network folders added to this PC ({folders.Count - drives} locations, {drives} mapped drives)";
            AddedFoldersEmptyText.Text = "No network locations or mapped network drives have been added. Use Add to File Explorer on a share below.";
            AddedFoldersEmptyText.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TestAllAddedFoldersButton.IsEnabled = folders.Count > 0;
            if (testAll)
            {
                await TestAddedFoldersAsync(folders);
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Added network folders", exception);
            AddedFoldersEmptyText.Text = $"Could not read the network folders added to this PC: {exception.Message}";
            AddedFoldersEmptyText.Visibility = Visibility.Visible;
        }
    }

    private async void TestAllAddedFoldersButton_Click(object sender, RoutedEventArgs e)
    {
        if (AddedFoldersList.ItemsSource is IReadOnlyList<AddedNetworkFolder> folders)
        {
            await TestAddedFoldersAsync(folders);
        }
    }

    private async Task TestAddedFoldersAsync(IReadOnlyList<AddedNetworkFolder> folders)
    {
        TestAllAddedFoldersButton.IsEnabled = false;
        try
        {
            await Task.WhenAll(folders.Select(folder => SharedFoldersService.TestConnectionAsync(folder)));
            int connected = folders.Count(folder => folder.State == ConnectionState.Connected);
            if (folders.Count > 0)
            {
                StatusText.Text = $"Network folders tested: {connected} of {folders.Count} connected.";
            }
        }
        finally
        {
            TestAllAddedFoldersButton.IsEnabled = folders.Count > 0;
        }
    }

    private async void TestAddedFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AddedNetworkFolder folder } element)
        {
            return;
        }

        element.IsEnabled = false;
        try
        {
            await SharedFoldersService.TestConnectionAsync(folder);
            StatusText.Text = $"{folder.Title.Trim()}: {folder.StatusText}";
        }
        finally
        {
            element.IsEnabled = true;
        }
    }

    private async void RemoveAddedFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AddedNetworkFolder folder } element)
        {
            return;
        }

        string question = folder.Kind == AddedFolderKind.MappedDrive
            ? $"Disconnect mapped drive {folder.DriveLetter} ({folder.Target})?\n\nThe drive is removed from File Explorer and will not reconnect at sign-in. Files on the network are not touched. Close any files open from this drive first."
            : $"Remove \"{folder.Name}\" ({folder.Target}) from This PC > Network locations?\n\nOnly the shortcut is removed. The shared folder and its files are not touched.";
        if (WpfMessageBox.Show(this, question, "Remove network folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        element.IsEnabled = false;
        try
        {
            FolderRemoveResult result = await _sharedFoldersService.RemoveAddedFolderAsync(folder);
            ApplicationLogService.WriteMessage("Added network folders", result.Message);
            StatusText.Text = result.Message;
            if (!result.Removed)
            {
                WpfMessageBox.Show(this, result.Message, "Remove network folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Remove network folder", exception);
            WpfMessageBox.Show(this, exception.Message, "Remove network folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            element.IsEnabled = true;
            await RefreshAddedFoldersAsync(testAll: true);
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
            int nas = shares.Where(share => share.IsNas).Select(share => share.Address).Distinct().Count();
            string nasText = nas > 0 ? $", {nas} NAS" : string.Empty;
            NetworkSharesHeaderText.Text = $"Shared on the network ({shares.Count(share => !share.NeedsSignIn && share.ShareName.Length > 0)} shares on {pcs} devices{nasText})";
            NetworkSharesEmptyText.Text = $"No PCs or NAS boxes with shared folders answered on this PC's networks. Last scan {DateTime.Now:t}. A NAS on a different network (VLAN) or with file sharing turned off cannot be found from here.";
            NetworkSharesEmptyText.Visibility = shares.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"Network share scan finished: {pcs} devices with shared folders{nasText}.";
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

    /// <summary>Opens a NAS's web admin page (DSM, QTS, TrueNAS and so on) in the default browser.</summary>
    private void OpenNasAdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                WpfMessageBox.Show(this, exception.Message, "Open web admin", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
            _ = RefreshAddedFoldersAsync(testAll: true);
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
