using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Management;
using System.Net;
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
    private readonly ConcurrentDictionary<string, string> _viewerNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly RemoteAlertTracker _remoteAlertTracker = new();
    private readonly List<RemoteAlert> _recentRemoteAlerts = new();
    private DispatcherTimer? _remotePcsTimer;
    private bool _remotePcsPageVisible;
    private bool _pollingRemotePcs;
    private bool _loadingRemoteSharing;
    private bool _findingRemotePcs;

    /// <summary>The hidden code was re-entered on this visit to the page, so it can be shown and changed.</summary>
    private bool _sharingCodeUnlocked;

    /// <summary>When the code was last saved; older snapshots still list the previous code's viewers.</summary>
    private DateTime _sharingCodeSavedAt = DateTime.MinValue;

    private List<RemoteViewerInfo> _sharingViewers = new();

    private void LoadRemoteMonitoringPage()
    {
        _loadingRemoteSharing = true;
        RemoteSharingToggle.IsChecked = _appSettings.RemoteSharingEnabled;
        _loadingRemoteSharing = false;

        _sharingCodeUnlocked = false;
        _loadingRemoteSharing = true;
        RemoteAlertsToggle.IsChecked = _appSettings.RemoteAlertsEnabled;
        RemoteAlertOverheatCheck.IsChecked = _appSettings.RemoteAlertOverheat;
        RemoteAlertDiskCheck.IsChecked = _appSettings.RemoteAlertDisk;
        RemoteAlertMemoryCheck.IsChecked = _appSettings.RemoteAlertMemory;
        RemoteAlertOfflineCheck.IsChecked = _appSettings.RemoteAlertOffline;
        _loadingRemoteSharing = false;
        UpdateRemoteAlertPanel();
        RemotePcsList.ItemsSource ??= _remotePcViews;
        SyncRemotePcViews();
        UpdateSharingPanel(resetCodeBox: true);
        _ = RefreshSharingNetworkTextAsync();
        _ = PollRemotePcsAsync();
    }

    /// <summary>At startup: create the tiles and, when notifications are on, start checking in the background.</summary>
    private void StartRemotePcMonitoring()
    {
        SyncRemotePcViews();
        UpdateRemotePollingTimer();
    }

    private void SetRemotePcsPolling(bool pageVisible)
    {
        _remotePcsPageVisible = pageVisible;
        UpdateRemotePollingTimer();
    }

    /// <summary>
    /// Every 5 seconds while the Network PCs page is open; otherwise every 30 seconds when problem notifications are
    /// on (also while the dashboard is only in the notification area); otherwise not at all.
    /// </summary>
    private void UpdateRemotePollingTimer()
    {
        bool background = _appSettings.RemoteAlertsEnabled && _appSettings.RemotePcs.Count > 0;
        if (!_remotePcsPageVisible && !background)
        {
            _remotePcsTimer?.Stop();
            return;
        }

        _remotePcsTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            async (_, _) => await PollRemotePcsAsync(), Dispatcher);
        TimeSpan interval = TimeSpan.FromSeconds(_remotePcsPageVisible ? 5 : 30);
        if (_remotePcsTimer.Interval != interval)
        {
            _remotePcsTimer.Interval = interval;
        }

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
        if (_remotePcsPageVisible)
        {
            // Sharing status comes from the service snapshot, so keep it current even with no PCs added.
            await RefreshSharingStatusAsync();
        }

        if (_pollingRemotePcs || _remotePcViews.Count == 0)
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
                    if (_appSettings.RemoteAlertsEnabled)
                    {
                        foreach (RemoteAlert alert in _remoteAlertTracker.Evaluate(entry, view.Title, result, _appSettings, DateTime.Now))
                        {
                            ShowRemoteAlert(alert);
                        }
                    }
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

    // ---- This PC: sharing ----------------------------------------------------------------------------------

    private async void RemoteSharingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingRemoteSharing)
        {
            return;
        }

        bool enabled = RemoteSharingToggle.IsChecked == true;
        if (!enabled && _sharingViewers.Count > 0 &&
            WpfMessageBox.Show(this,
                $"Stop sharing this PC's stats?\n\n{DescribeViewers()} monitor{(_sharingViewers.Count == 1 ? "s" : "")} this PC and will show it as not sharing.",
                "Network PCs", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            _loadingRemoteSharing = true;
            RemoteSharingToggle.IsChecked = true;
            _loadingRemoteSharing = false;
            return;
        }

        if (enabled && string.IsNullOrWhiteSpace(_appSettings.RemoteSharingCode))
        {
            _appSettings.RemoteSharingCode = RemoteMonitoringProtocol.NewPairingCode();
            _sharingCodeSavedAt = DateTime.Now;
        }

        _appSettings.RemoteSharingEnabled = enabled;
        await SaveRemoteSettingsAsync();
        ApplicationLogService.WriteMessage("Network PCs", enabled ? "Sharing this PC's stats turned on." : "Sharing this PC's stats turned off.");
        StatusText.Text = enabled
            ? "Sharing turned on. Other PCs can add this PC with the pairing code shown."
            : "Sharing turned off. Other PCs can no longer read this PC's stats.";
        UpdateSharingPanel(resetCodeBox: true);
        _ = RefreshSharingNetworkTextAsync();
    }

    private bool IsSharingCodeHidden => _appSettings.RemoteSharingCodeLocked && !_sharingCodeUnlocked;

    private void UpdateSharingPanel(bool resetCodeBox = false)
    {
        bool enabled = _appSettings.RemoteSharingEnabled;
        SharingDetailsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        bool hidden = IsSharingCodeHidden;
        SharingCodeLockedPanel.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        SharingCodeEditPanel.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        if (hidden || resetCodeBox)
        {
            SharingCodeBox.Text = hidden ? string.Empty : _appSettings.RemoteSharingCode;
        }

        SharingCodeHintText.Text = hidden
            ? "Hidden because a PC is monitoring this one with it. Show or change asks for the current code first."
            : _appSettings.RemoteSharingCodeLocked
                ? "PCs monitoring this one use this code; changing it disconnects them until they are given the new code."
                : $"Type your own code to use the same one on several PCs (at least {RemoteMonitoringProtocol.MinimumCodeLength} letters or numbers; capitals and dashes do not matter). It is hidden once a PC connects with it.";
        UpdateSaveSharingCodeButton();

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

    private void SharingCodeBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateSaveSharingCodeButton();

    private void UpdateSaveSharingCodeButton()
    {
        string typed = RemoteMonitoringProtocol.NormalizeCode(SharingCodeBox.Text);
        SaveSharingCodeButton.IsEnabled = !IsSharingCodeHidden
            && typed.Length >= RemoteMonitoringProtocol.MinimumCodeLength
            && typed != RemoteMonitoringProtocol.NormalizeCode(_appSettings.RemoteSharingCode);
    }

    private async void SaveSharingCodeButton_Click(object sender, RoutedEventArgs e)
    {
        string code = SharingCodeBox.Text.Trim();
        if (RemoteMonitoringProtocol.NormalizeCode(code).Length < RemoteMonitoringProtocol.MinimumCodeLength)
        {
            WpfMessageBox.Show(this, $"Use at least {RemoteMonitoringProtocol.MinimumCodeLength} letters or numbers.", "Pairing code", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_appSettings.RemoteSharingCodeLocked &&
            WpfMessageBox.Show(this,
                $"Change the pairing code?\n\n{(_sharingViewers.Count > 0 ? DescribeViewers() : "PCs that monitor this one")} will show \"Wrong pairing code\" until you enter the new code on them.",
                "Pairing code", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _appSettings.RemoteSharingCode = code;
        _appSettings.RemoteSharingCodeLocked = false;
        _sharingCodeSavedAt = DateTime.Now;
        _sharingCodeUnlocked = false;
        _sharingViewers = new List<RemoteViewerInfo>();
        await SaveRemoteSettingsAsync();
        ApplicationLogService.WriteMessage("Network PCs", "Pairing code changed.");
        StatusText.Text = "Pairing code saved. Use it when adding this PC on the primary PC.";
        UpdateSharingPanel(resetCodeBox: true);
        UpdateViewersText();
    }

    private void NewSharingCodeButton_Click(object sender, RoutedEventArgs e)
    {
        SharingCodeBox.Text = RemoteMonitoringProtocol.NewPairingCode();
        SharingCodeBox.Focus();
        StatusText.Text = "Random code filled in. Select Save code to use it.";
    }

    private void CopySharingCodeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(SharingCodeBox.Text.Trim());
            StatusText.Text = "Pairing code copied.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = "The clipboard is busy. Try Copy again.";
        }
    }

    private void UnlockSharingCodeButton_Click(object sender, RoutedEventArgs e)
    {
        CodePromptWindow prompt = new(
            "Pairing code",
            "Enter the current code",
            "The pairing code is hidden because a PC is monitoring this one with it. Enter it to show or change it.",
            "Show code",
            code => RemoteMonitoringProtocol.CodesMatch(code, _appSettings.RemoteSharingCode) ? null : "That is not the current code.",
            masked: true,
            offerForgot: true)
        { Owner = this };

        if (prompt.ShowDialog() == true)
        {
            _sharingCodeUnlocked = true;
            UpdateSharingPanel(resetCodeBox: true);
            return;
        }

        if (prompt.ForgotRequested &&
            WpfMessageBox.Show(this,
                "Set a new pairing code instead?\n\nType the new code and select Save code. PCs monitoring this one will need the new code too.",
                "Pairing code", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            _sharingCodeUnlocked = true;
            UpdateSharingPanel();
            SharingCodeBox.Text = string.Empty;
            SharingCodeBox.Focus();
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
            return;
        }

        if (snapshot.RemoteSharingActive)
        {
            SetSharingStatus(snapshot.RemoteSharingStatus, "SuccessBrush");
        }
        else
        {
            SetSharingStatus(snapshot.RemoteSharingStatus == "Off" ? "Starting..." : snapshot.RemoteSharingStatus,
                snapshot.RemoteSharingStatus == "Off" ? "MutedTextBrush" : "DangerBrush");
        }

        // A snapshot written before the last code change still lists the previous code's viewers.
        if (snapshot.UpdatedAt > _sharingCodeSavedAt.AddSeconds(3))
        {
            _sharingViewers = snapshot.RemoteViewers;
            UpdateViewersText();
            if (_sharingViewers.Count > 0 && !_appSettings.RemoteSharingCodeLocked)
            {
                // A PC now uses this code: hide it so it cannot be changed by accident.
                _appSettings.RemoteSharingCodeLocked = true;
                _sharingCodeUnlocked = false;
                await SaveRemoteSettingsAsync();
                UpdateSharingPanel();
            }
        }
    }

    private void UpdateViewersText()
    {
        if (_sharingViewers.Count == 0)
        {
            SharingViewersText.Text = "No PC has connected yet.";
            return;
        }

        SharingViewersText.Text = string.Join("\n", _sharingViewers.Select(viewer =>
        {
            DateTime lastSeen = viewer.LastSeenUtc.ToLocalTime();
            string when = DateTime.Now - lastSeen < TimeSpan.FromMinutes(1) ? "connected now" : $"last seen {lastSeen:dd/MM HH:mm}";
            return $"{ViewerName(viewer.Address)}  •  {when}";
        }));
    }

    private string DescribeViewers() =>
        _sharingViewers.Count == 1
            ? ViewerName(_sharingViewers[0].Address)
            : $"{_sharingViewers.Count} PCs ({string.Join(", ", _sharingViewers.Select(viewer => ViewerName(viewer.Address)))})";

    /// <summary>"192.168.1.118 (OFFICE-PC)" once the name is looked up; the address alone until then.</summary>
    private string ViewerName(string address)
    {
        if (_viewerNames.TryGetValue(address, out string? name))
        {
            return name.Length == 0 ? address : $"{address} ({name})";
        }

        _viewerNames[address] = string.Empty;
        _ = Task.Run(async () =>
        {
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
                IPHostEntry entry = await Dns.GetHostEntryAsync(address, timeout.Token);
                string host = entry.HostName.Split('.')[0];
                if (host.Length > 0 && !IPAddress.TryParse(entry.HostName, out _))
                {
                    _viewerNames[address] = host.ToUpperInvariant();
                    await Dispatcher.InvokeAsync(UpdateViewersText);
                }
            }
            catch (Exception)
            {
                // Shown by address only.
            }
        });
        return address;
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

    // ---- Monitored PCs ---------------------------------------------------------------------------------------

    private async void AddRemotePcButton_Click(object sender, RoutedEventArgs e) => await AddRemotePcAsync(null, null);

    private async Task AddRemotePcAsync(string? host, string? name)
    {
        RemotePcEditorWindow editor = new(_remoteMonitorClient, host: host, name: name, code: _appSettings.RemoteNetworkCode) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        _appSettings.RemotePcs.Add(editor.Result);
        if (string.IsNullOrWhiteSpace(_appSettings.RemoteNetworkCode))
        {
            // The first code used becomes the network code that Add all and the next Add PC start from.
            _appSettings.RemoteNetworkCode = editor.Result.PairingCode;
        }

        await SaveRemoteSettingsAsync();
        ApplicationLogService.WriteMessage("Network PCs", $"Added {editor.Result.Host} to monitored PCs.");
        SyncRemotePcViews();
        UpdateRemotePollingTimer();
        RemoveFromFoundList(new[] { editor.Result.Host });
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
        _remoteAlertTracker.Forget(view.Entry.Id);
        await SaveRemoteSettingsAsync();
        UpdateRemotePollingTimer();
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
        AddAllFoundPcsButton.Visibility = Visibility.Collapsed;
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

            ShowFoundPcs(candidates, found.Count > candidates.Count
                ? "Every PC found is already added (or is this PC)."
                : "No other PCs found. On each PC to watch: update Windows Utility, open Network PCs and turn on \"Allow this PC to be monitored\". Its network must be set to Private.");
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

    private void ShowFoundPcs(List<DiscoveredRemotePc> candidates, string emptyText)
    {
        FoundPcsList.ItemsSource = candidates;
        FoundPcsHeaderText.Text = $"Found on the network ({candidates.Count})";
        FoundPcsEmptyText.Text = emptyText;
        FoundPcsEmptyText.Visibility = candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddAllFoundPcsButton.Visibility = candidates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AddFoundPcButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DiscoveredRemotePc pc })
        {
            await AddRemotePcAsync(pc.Address, pc.Hello.MachineName);
        }
    }

    /// <summary>Adds every PC found that accepts the network code; asks for the code once and remembers it.</summary>
    private async void AddAllFoundPcsButton_Click(object sender, RoutedEventArgs e)
    {
        if (FoundPcsList.ItemsSource is not List<DiscoveredRemotePc> candidates || candidates.Count == 0)
        {
            return;
        }

        CodePromptWindow prompt = new(
            "Network code",
            $"Add {candidates.Count} PC{(candidates.Count == 1 ? "" : "s")}",
            "Enter the pairing code set on those PCs. Every PC that accepts it is added. The code is remembered on this PC for next time.",
            "Add all",
            code => RemoteMonitoringProtocol.NormalizeCode(code).Length >= RemoteMonitoringProtocol.MinimumCodeLength
                ? null
                : $"Use at least {RemoteMonitoringProtocol.MinimumCodeLength} letters or numbers.",
            initialCode: _appSettings.RemoteNetworkCode)
        { Owner = this };
        if (prompt.ShowDialog() != true)
        {
            return;
        }

        string code = prompt.Code;
        _appSettings.RemoteNetworkCode = code;
        AddAllFoundPcsButton.IsEnabled = false;
        FoundPcsHeaderText.Text = $"Adding {candidates.Count} PC{(candidates.Count == 1 ? "" : "s")}...";
        try
        {
            (DiscoveredRemotePc Pc, RemoteStatsResult Result)[] results = await Task.WhenAll(candidates.Select(async pc =>
                (pc, await _remoteMonitorClient.GetStatsAsync(pc.Address, RemoteMonitoringProtocol.DefaultPort, code))));

            List<DiscoveredRemotePc> added = new();
            List<string> failed = new();
            foreach ((DiscoveredRemotePc pc, RemoteStatsResult result) in results)
            {
                if (result.State == RemoteStatsState.Online)
                {
                    _appSettings.RemotePcs.Add(new RemotePcEntry
                    {
                        DisplayName = result.Stats?.MachineName ?? pc.Hello.MachineName,
                        Host = pc.Address,
                        Port = RemoteMonitoringProtocol.DefaultPort,
                        PairingCode = code
                    });
                    added.Add(pc);
                }
                else
                {
                    failed.Add($"{pc.Hello.MachineName} ({pc.Address}): {result.Message}");
                }
            }

            await SaveRemoteSettingsAsync();
            ApplicationLogService.WriteMessage("Network PCs", $"Add all: {added.Count} added, {failed.Count} not added.");
            SyncRemotePcViews();
            UpdateRemotePollingTimer();
            RemoveFromFoundList(added.Select(pc => pc.Address));
            StatusText.Text = $"{added.Count} PC{(added.Count == 1 ? "" : "s")} added.";
            if (failed.Count > 0)
            {
                WpfMessageBox.Show(this,
                    $"{added.Count} PC{(added.Count == 1 ? " was" : "s were")} added. These were not:\n\n{string.Join("\n", failed)}\n\nThose PCs use a different code. Add them one at a time with their own code.",
                    "Add all", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            await PollRemotePcsAsync();
        }
        finally
        {
            AddAllFoundPcsButton.IsEnabled = true;
        }
    }

    private void RemoveFromFoundList(IEnumerable<string> hosts)
    {
        if (FoundPcsList.ItemsSource is List<DiscoveredRemotePc> found)
        {
            HashSet<string> remove = hosts.ToHashSet(StringComparer.OrdinalIgnoreCase);
            ShowFoundPcs(found.Where(pc => !remove.Contains(pc.Address)).ToList(), "Every PC found is added.");
        }
    }

    private void SpeedTestRemotePcButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RemotePcView view })
        {
            new RemoteSpeedTestWindow(_remoteMonitorClient, view.Entry, view.Title) { Owner = this }.ShowDialog();
        }
    }

    // ---- Problem notifications ---------------------------------------------------------------------------

    private async void RemoteAlertSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingRemoteSharing)
        {
            return;
        }

        _appSettings.RemoteAlertsEnabled = RemoteAlertsToggle.IsChecked == true;
        _appSettings.RemoteAlertOverheat = RemoteAlertOverheatCheck.IsChecked == true;
        _appSettings.RemoteAlertDisk = RemoteAlertDiskCheck.IsChecked == true;
        _appSettings.RemoteAlertMemory = RemoteAlertMemoryCheck.IsChecked == true;
        _appSettings.RemoteAlertOffline = RemoteAlertOfflineCheck.IsChecked == true;
        await SaveRemoteSettingsAsync();
        UpdateRemoteAlertPanel();
        UpdateRemotePollingTimer();
    }

    private void UpdateRemoteAlertPanel()
    {
        RemoteAlertOptionsPanel.IsEnabled = _appSettings.RemoteAlertsEnabled;
        RemoteAlertOptionsPanel.Opacity = _appSettings.RemoteAlertsEnabled ? 1 : 0.5;
        RemoteAlertsNoteText.Text = !_appSettings.RemoteAlertsEnabled
            ? "Off: problems still show on the tiles, but nothing is announced."
            : _appSettings.DesktopNotificationsEnabled && _appSettings.TrayIconEnabled
                ? "Checked every 30 seconds in the background (every 5 seconds while this page is open), also while the dashboard is only in the notification area. Each problem is announced once, and again only after it has cleared."
                : "Desktop notifications or the notification-area icon are turned off in Settings, so problems are listed below and in the log instead of popping up.";
        UpdateRecentRemoteAlertsText();
    }

    private void ShowRemoteAlert(RemoteAlert alert)
    {
        ApplicationLogService.WriteMessage("Network PCs alert", $"{alert.Title}: {alert.Message}");
        _recentRemoteAlerts.Insert(0, alert);
        if (_recentRemoteAlerts.Count > 5)
        {
            _recentRemoteAlerts.RemoveAt(_recentRemoteAlerts.Count - 1);
        }

        UpdateRecentRemoteAlertsText();
        StatusText.Text = $"{alert.Title}. {alert.Message}";
        if (_appSettings.DesktopNotificationsEnabled && _trayIcon is { Visible: true })
        {
            _trayIcon.ShowBalloonTip(8000, alert.Title, alert.Message,
                alert.IsRecovery ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    private void UpdateRecentRemoteAlertsText()
    {
        RecentRemoteAlertsText.Visibility = _recentRemoteAlerts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentRemoteAlertsText.Text = "Recent: " + string.Join("\n", _recentRemoteAlerts.Select(alert => $"{alert.At:HH:mm}  {alert.Title}"));
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
