using System.Collections.ObjectModel;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using CentricDeviceMonitor.Dialogs;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor;

/// <summary>
/// Network PCs page: share this PC's stats with other PCs (served by the background service), and watch other PCs
/// that share theirs as live tiles.
/// </summary>
public partial class MainWindow
{
    private readonly RemoteMonitorClient _remoteMonitorClient = new();
    private readonly ObservableCollection<RemotePcView> _remotePcViews = new();
    private DispatcherTimer? _remotePcsTimer;
    private bool _pollingRemotePcs;
    private bool _loadingRemoteSharing;
    private bool _findingRemotePcs;

    private void LoadRemoteMonitoringPage()
    {
        _loadingRemoteSharing = true;
        RemoteSharingToggle.IsChecked = _appSettings.RemoteSharingEnabled;
        _loadingRemoteSharing = false;

        RemotePcsList.ItemsSource ??= _remotePcViews;
        SyncRemotePcViews();
        UpdateSharingPanel();
        _ = RefreshSharingNetworkTextAsync();
        _ = PollRemotePcsAsync();
    }

    /// <summary>Polls every 5 seconds while the page is open, and stops when another page is shown.</summary>
    private void SetRemotePcsPolling(bool active)
    {
        if (!active)
        {
            _remotePcsTimer?.Stop();
            return;
        }

        _remotePcsTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            async (_, _) => await PollRemotePcsAsync(), Dispatcher);
        _remotePcsTimer.Start();
    }

    private void SyncRemotePcViews()
    {
        foreach (RemotePcView view in _remotePcViews.Where(view => _appSettings.RemotePcs.All(pc => pc.Id != view.Entry.Id)).ToList())
        {
            _remotePcViews.Remove(view);
        }

        foreach (RemotePcEntry entry in _appSettings.RemotePcs.Where(pc => _remotePcViews.All(view => view.Entry.Id != pc.Id)))
        {
            _remotePcViews.Add(new RemotePcView(entry));
        }

        RemotePcsCountText.Text = _remotePcViews.Count == 0 ? string.Empty : $"{_remotePcViews.Count} PC{(_remotePcViews.Count == 1 ? "" : "s")}";
        RemotePcsEmptyText.Visibility = _remotePcViews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task PollRemotePcsAsync()
    {
        // Sharing status comes from the service snapshot, so keep it current even with no PCs added.
        await RefreshSharingStatusAsync();

        if (_pollingRemotePcs || _remotePcViews.Count == 0 || !IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }

        _pollingRemotePcs = true;
        try
        {
            await Task.WhenAll(_remotePcViews.ToList().Select(async view =>
            {
                RemotePcEntry entry = view.Entry;
                RemoteStatsResult result = await _remoteMonitorClient.GetStatsAsync(entry.Host, entry.Port, entry.PairingCode);
                if (ReferenceEquals(entry, view.Entry))
                {
                    view.Update(result);
                }
            }));
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Network PCs poll", exception);
        }
        finally
        {
            _pollingRemotePcs = false;
        }
    }

    private async void RemoteSharingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingRemoteSharing)
        {
            return;
        }

        bool enabled = RemoteSharingToggle.IsChecked == true;
        if (enabled && string.IsNullOrWhiteSpace(_appSettings.RemoteSharingCode))
        {
            _appSettings.RemoteSharingCode = RemoteMonitoringProtocol.NewPairingCode();
        }

        _appSettings.RemoteSharingEnabled = enabled;
        await SaveRemoteSettingsAsync();
        ApplicationLogService.WriteMessage("Network PCs", enabled ? "Sharing this PC's stats turned on." : "Sharing this PC's stats turned off.");
        StatusText.Text = enabled
            ? "Sharing turned on. Other PCs can add this PC with the pairing code shown."
            : "Sharing turned off. Other PCs can no longer read this PC's stats.";
        UpdateSharingPanel();
        _ = RefreshSharingNetworkTextAsync();
    }

    private void UpdateSharingPanel()
    {
        bool enabled = _appSettings.RemoteSharingEnabled;
        SharingDetailsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        SharingCodeText.Text = _appSettings.RemoteSharingCode;

        List<string> addresses = GetLocalIpv4Addresses();
        string port = _appSettings.RemoteSharingPort == RemoteMonitoringProtocol.DefaultPort
            ? string.Empty
            : $"  •  port {_appSettings.RemoteSharingPort}";
        SharingAddressText.Text = addresses.Count == 0
            ? $"{Environment.MachineName}{port}"
            : $"{string.Join(" or ", addresses)}  (or the PC name {Environment.MachineName}){port}";

        if (!enabled)
        {
            SetSharingStatus("Sharing is off", "MutedTextBrush");
        }
    }

    private async Task RefreshSharingStatusAsync()
    {
        if (!_appSettings.RemoteSharingEnabled)
        {
            return;
        }

        if (!IsBackgroundServiceRunning)
        {
            SetSharingStatus("Background service not running: sharing needs it (System Stats > Background service)", "DangerBrush");
            return;
        }

        BackgroundServiceSnapshot? snapshot = await _backgroundRuntimeStore.LoadAsync();
        if (snapshot is null || DateTime.Now - snapshot.UpdatedAt > TimeSpan.FromMinutes(1))
        {
            SetSharingStatus("Waiting for the background service...", "MutedTextBrush");
        }
        else if (snapshot.RemoteSharingActive)
        {
            SetSharingStatus(snapshot.RemoteSharingStatus, "SuccessBrush");
        }
        else
        {
            SetSharingStatus(snapshot.RemoteSharingStatus == "Off" ? "Starting..." : snapshot.RemoteSharingStatus,
                snapshot.RemoteSharingStatus == "Off" ? "MutedTextBrush" : "DangerBrush");
        }
    }

    private void SetSharingStatus(string text, string brushKey)
    {
        SharingStatusText.Text = text;
        SharingStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brushKey);
    }

    /// <summary>The firewall rule only opens the port on Private and Domain networks, so warn about Public ones.</summary>
    private async Task RefreshSharingNetworkTextAsync()
    {
        if (!_appSettings.RemoteSharingEnabled)
        {
            return;
        }

        List<(string Name, int Category)> profiles = await Task.Run(() =>
        {
            List<(string, int)> found = new();
            try
            {
                using ManagementObjectSearcher searcher = new(@"root\StandardCimv2", "SELECT Name, InterfaceAlias, NetworkCategory FROM MSFT_NetConnectionProfile");
                foreach (ManagementBaseObject profile in searcher.Get())
                {
                    using (profile)
                    {
                        string name = $"{profile["InterfaceAlias"]} ({profile["Name"]})";
                        int category = Convert.ToInt32(profile["NetworkCategory"] ?? 0);
                        found.Add((name, category));
                    }
                }
            }
            catch (ManagementException)
            {
            }

            return found;
        });

        if (profiles.Count == 0)
        {
            SharingNetworkText.Text = "No connected network.";
            SharingNetworkText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarningBrush");
            return;
        }

        List<string> publicNetworks = profiles.Where(profile => profile.Category == 0).Select(profile => profile.Name).ToList();
        if (publicNetworks.Count > 0)
        {
            SharingNetworkText.Text = $"{string.Join(", ", publicNetworks)} is set to Public, so other PCs cannot reach this one. If it is your home or office network, set it to Private in Settings > Network & internet > (your connection) > Network profile type.";
            SharingNetworkText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarningBrush");
        }
        else
        {
            SharingNetworkText.Text = string.Join(", ", profiles.Select(profile => $"{profile.Name}: {(profile.Category == 2 ? "Domain" : "Private")}")) + ". Other PCs on this network can reach this PC.";
            SharingNetworkText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextBrush");
        }
    }

    private static List<string> GetLocalIpv4Addresses()
    {
        List<string> addresses = new();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || SystemResourceMonitorService.IsVirtualAdapter(nic))
                {
                    continue;
                }

                addresses.AddRange(nic.GetIPProperties().UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => address.Address.ToString())
                    .Where(address => !address.StartsWith("169.254.", StringComparison.Ordinal)));
            }
        }
        catch (NetworkInformationException)
        {
        }

        return addresses.Distinct().ToList();
    }

    private void CopySharingCodeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_appSettings.RemoteSharingCode);
            StatusText.Text = "Pairing code copied.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = "The clipboard is busy. Try Copy again.";
        }
    }

    private async void NewSharingCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WpfMessageBox.Show(this,
                "Create a new pairing code?\n\nPCs that monitor this one will show \"Wrong pairing code\" until you enter the new code on them.",
                "New pairing code", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _appSettings.RemoteSharingCode = RemoteMonitoringProtocol.NewPairingCode();
        await SaveRemoteSettingsAsync();
        ApplicationLogService.WriteMessage("Network PCs", "Pairing code replaced.");
        UpdateSharingPanel();
        StatusText.Text = "New pairing code created.";
    }

    private async void AddRemotePcButton_Click(object sender, RoutedEventArgs e) => await AddRemotePcAsync(null, null);

    private async Task AddRemotePcAsync(string? host, string? name)
    {
        RemotePcEditorWindow editor = new(_remoteMonitorClient, host: host, name: name) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        _appSettings.RemotePcs.Add(editor.Result);
        await SaveRemoteSettingsAsync();
        ApplicationLogService.WriteMessage("Network PCs", $"Added {editor.Result.Host} to monitored PCs.");
        SyncRemotePcViews();
        RemoveFromFoundList(editor.Result.Host);
        await PollRemotePcsAsync();
    }

    private async void EditRemotePcButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RemotePcView view })
        {
            return;
        }

        RemotePcEditorWindow editor = new(_remoteMonitorClient, view.Entry) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        int index = _appSettings.RemotePcs.FindIndex(pc => pc.Id == view.Entry.Id);
        if (index >= 0)
        {
            _appSettings.RemotePcs[index] = editor.Result;
        }

        await SaveRemoteSettingsAsync();
        view.Reset(editor.Result);
        await PollRemotePcsAsync();
    }

    private async void RemoveRemotePcButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RemotePcView view })
        {
            return;
        }

        if (WpfMessageBox.Show(this, $"Stop monitoring {view.Title}?\n\nNothing changes on that PC; it just disappears from this page.",
                "Remove PC", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _appSettings.RemotePcs.RemoveAll(pc => pc.Id == view.Entry.Id);
        await SaveRemoteSettingsAsync();
        ApplicationLogService.WriteMessage("Network PCs", $"Removed {view.Entry.Host} from monitored PCs.");
        SyncRemotePcViews();
    }

    private async void FindRemotePcsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_findingRemotePcs)
        {
            return;
        }

        _findingRemotePcs = true;
        FindRemotePcsButton.IsEnabled = false;
        FoundPcsPanel.Visibility = Visibility.Visible;
        FoundPcsList.ItemsSource = null;
        FoundPcsHeaderText.Text = "Searching this network...";
        FoundPcsEmptyText.Text = "Looking for PCs running Windows Utility with sharing turned on. This takes a few seconds.";
        FoundPcsEmptyText.Visibility = Visibility.Visible;
        try
        {
            HashSet<string> own = GetLocalIpv4Addresses().ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<DiscoveredRemotePc> found = await _remoteMonitorClient.DiscoverAsync(RemoteMonitoringProtocol.DefaultPort);
            List<DiscoveredRemotePc> candidates = found
                .Where(pc => !own.Contains(pc.Address))
                .Where(pc => _appSettings.RemotePcs.All(existing =>
                    !string.Equals(existing.Host, pc.Address, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(existing.Host, pc.Hello.MachineName, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            FoundPcsList.ItemsSource = candidates;
            FoundPcsHeaderText.Text = $"Found on the network ({candidates.Count})";
            FoundPcsEmptyText.Text = found.Count > candidates.Count
                ? "Every PC found is already added (or is this PC)."
                : "No other PCs found. On each PC to watch: update Windows Utility, open Network PCs and turn on \"Allow this PC to be monitored\". Its network must be set to Private.";
            FoundPcsEmptyText.Visibility = candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"Network PC search finished: {candidates.Count} new PC{(candidates.Count == 1 ? "" : "s")} found.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Find network PCs", exception);
            FoundPcsHeaderText.Text = "Search failed";
            FoundPcsEmptyText.Text = exception.Message;
        }
        finally
        {
            FindRemotePcsButton.IsEnabled = true;
            _findingRemotePcs = false;
        }
    }

    private async void AddFoundPcButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DiscoveredRemotePc pc })
        {
            await AddRemotePcAsync(pc.Address, pc.Hello.MachineName);
        }
    }

    private void RemoveFromFoundList(string host)
    {
        if (FoundPcsList.ItemsSource is List<DiscoveredRemotePc> found)
        {
            List<DiscoveredRemotePc> remaining = found.Where(pc => !string.Equals(pc.Address, host, StringComparison.OrdinalIgnoreCase)).ToList();
            FoundPcsList.ItemsSource = remaining;
            FoundPcsHeaderText.Text = $"Found on the network ({remaining.Count})";
            FoundPcsEmptyText.Text = "Every PC found is added.";
            FoundPcsEmptyText.Visibility = remaining.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async Task SaveRemoteSettingsAsync()
    {
        try
        {
            await _settingsService.SaveAsync(_appSettings);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save Network PCs settings", exception);
            WpfMessageBox.Show(this, $"The setting could not be saved: {exception.Message}", "Network PCs", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
