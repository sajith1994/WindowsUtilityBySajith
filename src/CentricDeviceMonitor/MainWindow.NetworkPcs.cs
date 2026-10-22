using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CentricDeviceMonitor.Dialogs;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor;

/// <summary>
/// Network PCs page: share this PC's stats with other PCs (served by the background service), and watch other PCs
/// that share theirs as live tiles, with groups, search, history, software, waking, a daily summary and a phone page.
/// </summary>
public partial class MainWindow
{
    private readonly RemoteMonitorClient _remoteMonitorClient = new();
    private readonly ObservableCollection<RemotePcView> _remotePcViews = new();
    private readonly ConcurrentDictionary<string, string> _viewerNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly RemoteAlertTracker _remoteAlertTracker = new();
    private readonly List<RemoteAlert> _recentRemoteAlerts = new();
    private readonly RemoteAlertLog _remoteAlertLog = new();
    private bool _dailySummaryRunning;
    private bool _remotePcsViewReady;

    /// <summary>What clicking the last notification does (only for a minute after it was shown).</summary>
    private (Action Action, DateTime ShownAt)? _balloonClick;
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
        RemoteActionsToggle.IsChecked = _appSettings.RemoteActionsEnabled;
        _loadingRemoteSharing = false;

        _sharingCodeUnlocked = false;
        _loadingRemoteSharing = true;
        RemoteAlertsToggle.IsChecked = _appSettings.RemoteAlertsEnabled;
        RemoteAlertOverheatCheck.IsChecked = _appSettings.RemoteAlertOverheat;
        RemoteAlertDiskCheck.IsChecked = _appSettings.RemoteAlertDisk;
        RemoteAlertMemoryCheck.IsChecked = _appSettings.RemoteAlertMemory;
        RemoteAlertOfflineCheck.IsChecked = _appSettings.RemoteAlertOffline;
        RemoteAlertSecurityCheck.IsChecked = _appSettings.RemoteAlertSecurity;
        RemoteVpnToggle.IsChecked = _appSettings.RemoteSharingAllowVpn;
        DailySummaryToggle.IsChecked = _appSettings.DailySummaryEnabled;
        if (DailySummaryHourComboBox.Items.Count == 0)
        {
            for (int hour = 0; hour < 24; hour++)
            {
                DailySummaryHourComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = $"{hour:00}:00", Tag = hour });
            }
        }

        DailySummaryHourComboBox.SelectedIndex = Math.Clamp(_appSettings.DailySummaryHour, 0, 23);
        PhoneViewToggle.IsChecked = _appSettings.PhoneViewEnabled;
        _loadingRemoteSharing = false;
        UpdateRemoteAlertPanel();
        UpdateDailySummaryNote();
        UpdatePhoneViewPanel();
        RemotePcsList.ItemsSource ??= _remotePcViews;
        SetUpRemotePcsView();
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
        // The daily summary and the phone page also need current readings when the page is closed.
        bool background = _appSettings.RemotePcs.Count > 0
            && (_appSettings.RemoteAlertsEnabled || _appSettings.DailySummaryEnabled || _appSettings.PhoneViewEnabled);
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
        RemotePcsFilterBar.Visibility = _remotePcViews.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        UpdateGroupFilter();
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
            bool wakeDetailsChanged = false;
            await Task.WhenAll(_remotePcViews.ToList().Select(async view =>
            {
                RemotePcEntry entry = view.Entry;
                RemoteStatsResult result = await _remoteMonitorClient.GetStatsAsync(entry.Host, entry.Port, entry.PairingCode);
                if (ReferenceEquals(entry, view.Entry))
                {
                    view.Update(result);
                    wakeDetailsChanged |= view.RememberWakeDetails();
                    if (_appSettings.RemoteAlertsEnabled)
                    {
                        foreach (RemoteAlert alert in _remoteAlertTracker.Evaluate(entry, view.Title, result, _appSettings, DateTime.Now))
                        {
                            ShowRemoteAlert(alert);
                        }
                    }
                }
            }));

            if (wakeDetailsChanged)
            {
                // Adapter addresses are kept so the PC can be woken when it is off (they change rarely).
                await SaveRemoteSettingsAsync();
            }

            if (_appSettings.PhoneViewEnabled)
            {
                PhoneViewData data = PhoneViewService.Build(_remotePcViews, Environment.MachineName, DateTime.UtcNow);
                await Task.Run(() => PhoneViewService.Write(data));
            }

            UpdateNoMatchText();
            await MaybeShowDailySummaryAsync();
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
        RemoteActionsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        RemoteVpnPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateVpnText();

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
        if (_appSettings.RemoteSharingAllowVpn && GetVpnAddress() is { } vpnAddress)
        {
            SharingAddressText.Text += $"\nOver Tailscale: {vpnAddress}";
        }

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
                    .Where(address => !address.StartsWith("169.254.", StringComparison.Ordinal) && !VpnAddresses.IsVpnHost(address)));
            }
        }
        catch (NetworkInformationException)
        {
        }

        return addresses.Distinct().ToList();
    }

    // ---- Monitored PCs ---------------------------------------------------------------------------------------

    private async void AddRemotePcButton_Click(object sender, RoutedEventArgs e) => await AddRemotePcAsync(null, null);

    private IEnumerable<string> RemoteGroups => _appSettings.RemotePcs.Select(pc => pc.Group.Trim()).Where(group => group.Length > 0);

    private async Task AddRemotePcAsync(string? host, string? name)
    {
        RemotePcEditorWindow editor = new(_remoteMonitorClient, host: host, name: name, code: _appSettings.RemoteNetworkCode, groups: RemoteGroups) { Owner = this };
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

        RemotePcEditorWindow editor = new(_remoteMonitorClient, view.Entry, groups: RemoteGroups) { Owner = this };
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
        UpdateGroupFilter();
        RemotePcsCollectionView.Refresh();
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
            if (GetVpnAddress() is { } ownVpnAddress)
            {
                own.Add(ownVpnAddress);
            }

            // This network, plus PCs on this PC's Tailscale network when Tailscale is installed.
            Task<IReadOnlyList<DiscoveredRemotePc>> local = _remoteMonitorClient.DiscoverAsync(RemoteMonitoringProtocol.DefaultPort);
            Task<IReadOnlyList<DiscoveredRemotePc>> vpn = _remoteMonitorClient.DiscoverVpnAsync(RemoteMonitoringProtocol.DefaultPort);
            List<DiscoveredRemotePc> found = (await local).ToList();
            found.AddRange((await vpn).Where(pc => found.All(known => !string.Equals(known.Hello.MachineName, pc.Hello.MachineName, StringComparison.OrdinalIgnoreCase))));
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

    // ---- Inventory report ---------------------------------------------------------------------------------

    private void ExportInventoryReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_remotePcViews.Count == 0)
        {
            WpfMessageBox.Show(this, "Add PCs to monitor first; the report lists every monitored PC.", "Export report", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = "Export PC inventory report",
            FileName = $"PC inventory {DateTime.Now:yyyy-MM-dd}",
            Filter = "Printable report (*.html)|*.html|Spreadsheet for Excel (*.csv)|*.csv",
            DefaultExt = ".html",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            bool csv = dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            string content = csv
                ? InventoryReportService.BuildCsv(_remotePcViews)
                : InventoryReportService.BuildHtml(_remotePcViews, Environment.MachineName);
            // UTF-8 with BOM so Excel shows accented names and °C correctly.
            System.IO.File.WriteAllText(dialog.FileName, content, new System.Text.UTF8Encoding(true));
            ApplicationLogService.WriteMessage("Network PCs", $"Inventory report saved to {dialog.FileName}.");
            StatusText.Text = $"Inventory report saved: {dialog.FileName}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Export inventory report", exception);
            WpfMessageBox.Show(this, $"The report could not be saved: {exception.Message}", "Export report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    // ---- Updating monitored PCs -------------------------------------------------------------------------

    private async void UpdateRemotePcButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RemotePcView view })
        {
            await RequestRemoteUpdatesAsync(new[] { view });
        }
    }

    private async void UpdateAllRemotePcsButton_Click(object sender, RoutedEventArgs e)
    {
        List<RemotePcView> outdated = _remotePcViews.Where(view => view.CanUpdate).ToList();
        if (outdated.Count == 0)
        {
            int offline = _remotePcViews.Count(view => !view.HasStats);
            WpfMessageBox.Show(this,
                $"Every monitored PC that is online already has Windows Utility {UpdateService.GetCurrentVersion().ToString(3)} or newer (or is updating)."
                + (offline > 0 ? $"\n\n{offline} PC{(offline == 1 ? " is" : "s are")} offline and can be updated once back online." : string.Empty),
                "Update all", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RequestRemoteUpdatesAsync(outdated);
    }

    /// <summary>
    /// Asks each PC to update itself. Each PC checks the official release, downloads and verifies the installer and
    /// installs it silently; its tile shows the progress and then the new version.
    /// </summary>
    private async Task RequestRemoteUpdatesAsync(IReadOnlyList<RemotePcView> views)
    {
        string names = string.Join("\n", views.Select(view => $"•  {view.Title} ({view.Stats?.AppVersion})"));
        if (WpfMessageBox.Show(this,
                $"Update Windows Utility on {(views.Count == 1 ? "this PC" : $"these {views.Count} PCs")} to the latest release?\n\n{names}\n\n"
                + "Each PC downloads the official installer, checks it and installs it silently. Windows Utility closes on that PC for about a minute "
                + "and reopens for the signed-in user if it was open. Nothing else on the PC is changed.",
                "Update monitored PCs", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (RemotePcView view in views)
        {
            view.MarkUpdateRequested(true);
        }

        (RemotePcView View, RemoteUpdateResult? Result, string? Error)[] results = await Task.WhenAll(views.Select(async view =>
        {
            try
            {
                return (view, await _remoteMonitorClient.RequestUpdateAsync(view.Entry), (string?)null);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                return (view, (RemoteUpdateResult?)null, exception is TaskCanceledException ? "No answer in time." : exception.Message);
            }
        }));

        List<string> problems = new();
        int started = 0;
        foreach ((RemotePcView view, RemoteUpdateResult? result, string? error) in results)
        {
            if (result?.Status is "Started" or "Busy")
            {
                started++;
                continue;
            }

            view.MarkUpdateRequested(false);
            problems.Add($"{view.Title}: {(result?.Status == "UpToDate" ? $"already up to date ({result.CurrentVersion})" : error ?? result?.Message ?? "no answer")}");
        }

        ApplicationLogService.WriteMessage("Network PCs", $"Update requested on {views.Count} PC(s): {started} started. {string.Join(" | ", problems)}");
        StatusText.Text = $"Update started on {started} of {views.Count} PC{(views.Count == 1 ? "" : "s")}. Their tiles show the progress.";
        if (problems.Count > 0)
        {
            WpfMessageBox.Show(this,
                (started > 0 ? $"Update started on {started} PC{(started == 1 ? "" : "s")}.\n\n" : string.Empty) + "Not started:\n" + string.Join("\n", problems),
                "Update monitored PCs", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        await PollRemotePcsAsync();
    }
    private void ActionsRemotePcButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RemotePcView view })
        {
            new RemoteActionsWindow(_remoteMonitorClient, view) { Owner = this }.Show();
        }
    }

    private async void RemoteActionsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingRemoteSharing)
        {
            return;
        }

        bool enable = RemoteActionsToggle.IsChecked == true;
        if (enable && WpfMessageBox.Show(this,
                "Allow the PC monitoring this one to restart or shut down this PC, lock it, show messages, clear temporary files, start Windows Update checks and create restore points?\n\nOnly a PC with the pairing code can do this, every action is confirmed on that PC and logged here, and you can turn this off at any time.",
                "Allow actions", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            _loadingRemoteSharing = true;
            RemoteActionsToggle.IsChecked = false;
            _loadingRemoteSharing = false;
            return;
        }

        _appSettings.RemoteActionsEnabled = enable;
        await SaveRemoteSettingsAsync();
        ApplicationLogService.WriteMessage("Network PCs", enable ? "Actions from the main PC allowed." : "Actions from the main PC turned off.");
        StatusText.Text = enable ? "This PC now accepts actions from the PC monitoring it." : "This PC no longer accepts actions.";
    }
    private void DetailsRemotePcButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RemotePcView view })
        {
            new RemotePcDetailsWindow(_remoteMonitorClient, view.Entry, view.Title) { Owner = this }.Show();
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
        _appSettings.RemoteAlertSecurity = RemoteAlertSecurityCheck.IsChecked == true;
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
        _remoteAlertLog.Add(alert);
        _recentRemoteAlerts.Insert(0, alert);
        if (_recentRemoteAlerts.Count > 5)
        {
            _recentRemoteAlerts.RemoveAt(_recentRemoteAlerts.Count - 1);
        }

        UpdateRecentRemoteAlertsText();
        StatusText.Text = $"{alert.Title}. {alert.Message}";
        ShowBalloon(alert.Title, alert.Message, alert.IsRecovery ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning,
            () => ShowNetworkPcsPage());
    }

    /// <summary>A notification that runs <paramref name="onClick"/> when clicked (when notifications are allowed).</summary>
    private bool ShowBalloon(string title, string message, System.Windows.Forms.ToolTipIcon icon, Action? onClick)
    {
        if (!_appSettings.DesktopNotificationsEnabled || _trayIcon is not { Visible: true })
        {
            return false;
        }

        _balloonClick = onClick is null ? null : (onClick, DateTime.Now);
        _trayIcon.ShowBalloonTip(8000, title, message, icon);
        return true;
    }

    private void OnBalloonClicked()
    {
        if (_balloonClick is { } click && DateTime.Now - click.ShownAt < TimeSpan.FromMinutes(1))
        {
            _balloonClick = null;
            click.Action();
        }
    }

    private void UpdateRecentRemoteAlertsText()
    {
        RecentRemoteAlertsText.Visibility = _recentRemoteAlerts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentRemoteAlertsText.Text = "Recent: " + string.Join("\n", _recentRemoteAlerts.Select(alert => $"{alert.At:HH:mm}  {alert.Title}"));
    }

    private void ShowNetworkPcsPage()
    {
        ShowDashboardFromTray();
        NavigationList.SelectedIndex = 4;
    }

    // ---- Search, groups and sorting -----------------------------------------------------------------------

    private ListCollectionView RemotePcsCollectionView => (ListCollectionView)CollectionViewSource.GetDefaultView(_remotePcViews);

    private void SetUpRemotePcsView()
    {
        if (_remotePcsViewReady)
        {
            return;
        }

        _remotePcsViewReady = true;
        ListCollectionView view = RemotePcsCollectionView;
        view.Filter = item => item is RemotePcView pc && MatchesRemotePcFilter(pc);
        // Re-checked on every poll, so a search for a signed-in user follows who is signed in.
        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(RemotePcView.SearchText));
        ApplyRemotePcSort();
    }

    private bool MatchesRemotePcFilter(RemotePcView pc)
    {
        if (RemotePcGroupComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string group } && group != "*"
            && !string.Equals(pc.Group, group, StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        string[] words = RemotePcSearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string text = pc.SearchText;
        return words.All(word => text.Contains(word, StringComparison.CurrentCultureIgnoreCase));
    }

    private void RemotePcFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_remotePcsViewReady)
        {
            return;
        }

        RemotePcSearchHint.Visibility = RemotePcSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemotePcsCollectionView.Refresh();
        UpdateNoMatchText();
    }

    private void RemotePcSort_Changed(object sender, RoutedEventArgs e)
    {
        if (_remotePcsViewReady)
        {
            ApplyRemotePcSort();
        }
    }

    private void ApplyRemotePcSort()
    {
        string sort = RemotePcSortComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag } ? tag : "name";
        ListCollectionView view = RemotePcsCollectionView;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.LiveSortingProperties.Clear();
            if (sort == "problems")
            {
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(RemotePcView.SortRank), System.ComponentModel.ListSortDirection.Ascending));
                view.LiveSortingProperties.Add(nameof(RemotePcView.SortRank));
            }
            else if (sort == "group")
            {
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(RemotePcView.Group), System.ComponentModel.ListSortDirection.Ascending));
            }

            view.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(RemotePcView.Title), System.ComponentModel.ListSortDirection.Ascending));
            view.LiveSortingProperties.Add(nameof(RemotePcView.Title));
            view.IsLiveSorting = true;
        }
    }

    /// <summary>"All groups", each group used, and "No group" when some PCs have none.</summary>
    private void UpdateGroupFilter()
    {
        List<string> groups = RemoteGroups.Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase).ToList();
        string selected = RemotePcGroupComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string tag } ? tag : "*";
        List<(string Label, string Tag)> items = new() { ("All groups", "*") };
        items.AddRange(groups.Select(group => (group, group)));
        if (groups.Count > 0 && _appSettings.RemotePcs.Any(pc => pc.Group.Trim().Length == 0))
        {
            items.Add(("No group", string.Empty));
        }

        RemotePcGroupComboBox.Visibility = groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool same = RemotePcGroupComboBox.Items.Count == items.Count
            && RemotePcGroupComboBox.Items.Cast<System.Windows.Controls.ComboBoxItem>().Select(item => item.Tag as string).SequenceEqual(items.Select(item => item.Tag));
        if (same)
        {
            return;
        }

        RemotePcGroupComboBox.Items.Clear();
        foreach ((string label, string groupTag) in items)
        {
            RemotePcGroupComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = label, Tag = groupTag });
        }

        int index = items.FindIndex(item => string.Equals(item.Tag, selected, StringComparison.CurrentCultureIgnoreCase));
        RemotePcGroupComboBox.SelectedIndex = index >= 0 ? index : 0;
    }

    private void UpdateNoMatchText() =>
        RemotePcsNoMatchText.Visibility = _remotePcViews.Count > 0 && _remotePcsViewReady && RemotePcsCollectionView.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---- History, software and more ----------------------------------------------------------------------

    private void HistoryRemotePcButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RemotePcView view })
        {
            RemotePcEntry pc = view.Entry;
            new RemotePcHistoryWindow(view.Title, (days, token) => _remoteMonitorClient.GetHistoryAsync(pc, days, token),
                view.Stats?.CpuTemperatureIsCpuSpecific == true ? view.Stats.CpuTemperatureAlertThresholdCelsius : null) { Owner = this }.Show();
        }
    }

    private void MoreRemotePcButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void SoftwareRemotePcButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RemotePcView view })
        {
            RemotePcEntry pc = view.Entry;
            string name = view.Title;
            new SoftwareWindow($"Software: {name}", severalPcs: false,
                async token => new[] { await SoftwareWindow.ReadAsync(_remoteMonitorClient, pc, name, token) }) { Owner = this }.Show();
        }
    }

    private void AllSoftwareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_remotePcViews.Count == 0)
        {
            WpfMessageBox.Show(this, "Add PCs to monitor first.", "Software", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        List<(RemotePcEntry Pc, string Name)> pcs = _remotePcViews.Select(view => (view.Entry, view.Title)).ToList();
        new SoftwareWindow("Software on all monitored PCs", severalPcs: true, async token =>
        {
            using SemaphoreSlim limiter = new(6, 6);
            SoftwareSource[] sources = await Task.WhenAll(pcs.Select(async pc =>
            {
                await limiter.WaitAsync(token);
                try
                {
                    return await SoftwareWindow.ReadAsync(_remoteMonitorClient, pc.Pc, pc.Name, token);
                }
                finally
                {
                    limiter.Release();
                }
            }));
            return sources;
        }) { Owner = this }.Show();
    }

    // ---- Waking --------------------------------------------------------------------------------------------

    private async void WakeRemotePcButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RemotePcView view })
        {
            return;
        }

        int sent = await WakeOnLanService.SendAsync(view.Entry.WakeMacAddresses, view.Entry.LastKnownAddress);
        if (sent == 0)
        {
            WpfMessageBox.Show(this, "The wake signal could not be sent: this PC has no connected network.", "Wake", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        view.MarkWakeSent();
        ApplicationLogService.WriteMessage("Network PCs", $"Wake-on-LAN sent to {view.Title} ({string.Join(", ", view.Entry.WakeMacAddresses)}).");
        StatusText.Text = $"Wake signal sent to {view.Title}. A PC takes up to a few minutes to start; its tile turns green when it answers.";
    }

    // ---- VPN (Tailscale) -----------------------------------------------------------------------------------

    /// <summary>This PC's Tailscale IPv4 address, when it has one.</summary>
    private static string? GetVpnAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.ToString())
                .FirstOrDefault(VpnAddresses.IsVpnHost);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private void UpdateVpnText()
    {
        bool installed = RemoteMonitorClient.TailscaleExecutable() is not null;
        string? address = GetVpnAddress();
        RemoteVpnText.Text = (_appSettings.RemoteSharingAllowVpn ? "On: " : string.Empty)
            + "for a main PC at another site or at home. Install Tailscale (tailscale.com) on this PC and on the main PC and sign in with the same account; "
            + "this then opens the port to Tailscale addresses only (on any network). Add this PC on the main PC by its Tailscale name or address. "
            + (address is not null ? $"This PC's Tailscale address: {address}." : installed ? "Tailscale is installed but not connected." : "Tailscale is not installed on this PC.");
        RemoteVpnText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            _appSettings.RemoteSharingAllowVpn && address is null ? "WarningBrush" : "MutedTextBrush");
    }

    private async void RemoteVpnToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingRemoteSharing)
        {
            return;
        }

        _appSettings.RemoteSharingAllowVpn = RemoteVpnToggle.IsChecked == true;
        await SaveRemoteSettingsAsync();
        ApplicationLogService.WriteMessage("Network PCs", _appSettings.RemoteSharingAllowVpn ? "PCs on the Tailscale VPN allowed." : "VPN access turned off.");
        StatusText.Text = _appSettings.RemoteSharingAllowVpn
            ? "PCs on your Tailscale network can now reach this PC (with the pairing code)."
            : "Only PCs on the local network can reach this PC now.";
        UpdateSharingPanel();
    }

    // ---- Daily summary -----------------------------------------------------------------------------------

    private async void DailySummarySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingRemoteSharing)
        {
            return;
        }

        _appSettings.DailySummaryEnabled = DailySummaryToggle.IsChecked == true;
        if (DailySummaryHourComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: int hour })
        {
            _appSettings.DailySummaryHour = hour;
        }

        await SaveRemoteSettingsAsync();
        UpdateDailySummaryNote();
        UpdateRemotePollingTimer();
    }

    private void UpdateDailySummaryNote()
    {
        DailySummaryHourComboBox.IsEnabled = _appSettings.DailySummaryEnabled;
        DailySummaryNoteText.Text = _appSettings.DailySummaryEnabled
            ? $"Each day at {_appSettings.DailySummaryHour:00}:00 (or when the dashboard starts later that day) one notification sums up what needs attention, the last 24 hours of alerts and printers low on ink or toner. Clicking it opens a printable page."
            : "Off. Show summary now still works.";
    }

    private async void ShowDailySummaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_remotePcViews.Count == 0)
        {
            WpfMessageBox.Show(this, "Add PCs to monitor first.", "Daily summary", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await PollRemotePcsAsync();
        if (await BuildDailySummaryAsync() is { } path)
        {
            OpenFile(path);
        }
    }

    /// <summary>Once a day, from the chosen hour, after every PC has been checked at least once.</summary>
    private async Task MaybeShowDailySummaryAsync()
    {
        DateTime now = DateTime.Now;
        string today = now.ToString("yyyy-MM-dd");
        if (!_appSettings.DailySummaryEnabled || _remotePcViews.Count == 0 || now.Hour < _appSettings.DailySummaryHour
            || _appSettings.DailySummaryLastDate == today || _dailySummaryRunning || _remotePcViews.Any(view => view.StatusKind == "Checking"))
        {
            return;
        }

        _appSettings.DailySummaryLastDate = today;
        await SaveRemoteSettingsAsync();
        if (await BuildDailySummaryAsync() is not { } path)
        {
            return;
        }

        string headline = _lastDailySummaryHeadline;
        if (!ShowBalloon(headline, _lastDailySummaryMessage, System.Windows.Forms.ToolTipIcon.Info, () => OpenFile(path)))
        {
            StatusText.Text = $"{headline}. Open it with Show summary now on the Network PCs page.";
        }
    }

    private string _lastDailySummaryHeadline = "Daily summary";
    private string _lastDailySummaryMessage = string.Empty;

    /// <summary>Builds the summary (asking the printers for their levels) and saves it; returns the page's path.</summary>
    private async Task<string?> BuildDailySummaryAsync()
    {
        if (_dailySummaryRunning)
        {
            return null;
        }

        _dailySummaryRunning = true;
        try
        {
            List<(string Name, string Address)> printers = KnownNetworkPrinters()
                .Where(printer => IPAddress.TryParse(printer.Address, out _))
                .GroupBy(printer => printer.Address).Select(group => group.First()).ToList();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            PrinterSuppliesResult[] supplies = await Task.WhenAll(printers.Select(printer => PrinterSuppliesService.ReadAsync(printer.Address, timeout.Token)));
            DailySummary summary = DailySummaryService.Build(
                _remotePcViews.ToList(),
                _remoteAlertLog.Since(DateTime.Now.AddDays(-1)),
                printers.Zip(supplies, (printer, result) => (printer.Name, result)).ToList(),
                DateTime.Now,
                Environment.MachineName);

            string folder = System.IO.Path.Combine(SharedDataPaths.LogsDirectory, "daily-summaries");
            System.IO.Directory.CreateDirectory(folder);
            foreach (string old in System.IO.Directory.EnumerateFiles(folder, "daily-summary-*.html").OrderByDescending(file => file).Skip(30))
            {
                System.IO.File.Delete(old);
            }

            string path = System.IO.Path.Combine(folder, $"daily-summary-{DateTime.Now:yyyy-MM-dd}.html");
            await System.IO.File.WriteAllTextAsync(path, summary.Html, new System.Text.UTF8Encoding(true));
            _lastDailySummaryMessage = summary.Message;
            _lastDailySummaryHeadline = summary.Headline;
            ApplicationLogService.WriteMessage("Network PCs", $"{summary.Headline}. Saved to {path}.");
            return path;
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ApplicationLogService.WriteException("Daily summary", exception);
            StatusText.Text = $"The daily summary could not be made: {exception.Message}";
            return null;
        }
        finally
        {
            _dailySummaryRunning = false;
        }
    }

    private void OpenFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText.Text = $"Could not open {path}: {exception.Message}";
        }
    }

    // ---- Phone view ----------------------------------------------------------------------------------------

    private string PhoneLink => PhoneViewService.Link(GetLocalIpv4Addresses().FirstOrDefault() ?? Environment.MachineName, _appSettings.RemoteSharingPort, _appSettings.PhoneViewKey);

    private void UpdatePhoneViewPanel()
    {
        bool enabled = _appSettings.PhoneViewEnabled;
        PhoneViewDetailsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled)
        {
            return;
        }

        PhoneViewLinkBox.Text = PhoneLink;
        PhoneViewNoteText.Text = IsBackgroundServiceRunning
            ? "Open the link on a phone connected to the same network (Wi-Fi) as this PC; it refreshes every 15 seconds while this dashboard runs (also in the notification area). "
              + "Anyone with the link can see these tiles (read-only, nothing can be changed). New link makes the old one stop working. This PC's network must be set to Private."
            : "The background service is not running, so the phone page cannot be served. Start it on System Stats > Background service.";
        PhoneViewNoteText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, IsBackgroundServiceRunning ? "MutedTextBrush" : "DangerBrush");
    }

    private async void PhoneViewToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingRemoteSharing)
        {
            return;
        }

        bool enable = PhoneViewToggle.IsChecked == true;
        if (enable && string.IsNullOrWhiteSpace(_appSettings.PhoneViewKey))
        {
            _appSettings.PhoneViewKey = PhoneViewService.NewKey();
        }

        _appSettings.PhoneViewEnabled = enable;
        await SaveRemoteSettingsAsync();
        if (enable)
        {
            PhoneViewService.Write(PhoneViewService.Build(_remotePcViews, Environment.MachineName, DateTime.UtcNow));
        }
        else
        {
            PhoneViewService.Delete();
        }

        ApplicationLogService.WriteMessage("Network PCs", enable ? "Phone view turned on." : "Phone view turned off.");
        StatusText.Text = enable ? "Phone view on. Open the link shown on a phone on the same Wi-Fi." : "Phone view off. Phone links no longer work.";
        UpdatePhoneViewPanel();
        UpdateRemotePollingTimer();
    }

    private void CopyPhoneLinkButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(PhoneViewLinkBox.Text);
            StatusText.Text = "Phone link copied. Send it to your phone (for example by email or chat) and open it there.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = "The clipboard is busy. Try Copy link again.";
        }
    }

    private void OpenPhoneLinkButton_Click(object sender, RoutedEventArgs e) => OpenFile(PhoneViewLinkBox.Text);

    private async void NewPhoneLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (WpfMessageBox.Show(this, "Make a new phone link? The current link stops working on every phone that has it.",
                "Phone view", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _appSettings.PhoneViewKey = PhoneViewService.NewKey();
        await SaveRemoteSettingsAsync();
        ApplicationLogService.WriteMessage("Network PCs", "New phone view link made; the old link no longer works.");
        UpdatePhoneViewPanel();
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
