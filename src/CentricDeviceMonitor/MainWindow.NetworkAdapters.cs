using System.Windows;
using System.Windows.Controls;
using CentricDeviceMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor;

/// <summary>Network Diagnostics: per-adapter DHCP/static IPv4 and DNS settings.</summary>
public partial class MainWindow
{
    private readonly NetworkAdapterConfigService _adapterConfigService = new();
    private bool _applyingAdapterSettings;

    private void RefreshNetworkAdaptersButton_Click(object sender, RoutedEventArgs e) => RefreshNetworkAdapters();

    /// <summary>Opens Windows Network Connections (Control Panel > Network and Internet > Change adapter options).</summary>
    private void OpenWindowsNetworkConnectionsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe", "ncpa.cpl") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Open Network Connections", exception);
            WpfMessageBox.Show(this, exception.Message, "Network Connections", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Loads adapters, keeping the current selection when it still exists.</summary>
    private void RefreshNetworkAdapters()
    {
        if (DnsPresetComboBox.ItemsSource is null)
        {
            DnsPresetComboBox.ItemsSource = NetworkAdapterConfigService.DnsPresets;
        }

        string? selectedName = (NetworkAdapterComboBox.SelectedItem as NetworkAdapterInfo)?.Name;
        try
        {
            IReadOnlyList<NetworkAdapterInfo> adapters = _adapterConfigService.GetAdapters();
            NetworkAdapterComboBox.ItemsSource = adapters;
            NetworkAdapterComboBox.SelectedItem = adapters.FirstOrDefault(adapter => adapter.Name == selectedName) ?? adapters.FirstOrDefault();
            if (adapters.Count == 0)
            {
                NetworkAdapterCurrentText.Text = "No network adapters were found.";
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Network adapters", exception);
            NetworkAdapterCurrentText.Text = $"Could not read network adapters: {exception.Message}";
        }
    }

    private void NetworkAdapterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NetworkAdapterComboBox.SelectedItem is not NetworkAdapterInfo adapter)
        {
            return;
        }

        NetworkAdapterCurrentText.Text = adapter.Summary;

        // Start from the adapter's current settings so a small change does not require retyping everything.
        AdapterDhcpRadio.IsChecked = adapter.DhcpEnabled;
        AdapterStaticRadio.IsChecked = !adapter.DhcpEnabled;
        AdapterIpTextBox.Text = adapter.IPv4Address;
        AdapterMaskTextBox.Text = string.IsNullOrEmpty(adapter.SubnetMask) ? "255.255.255.0" : adapter.SubnetMask;
        AdapterGatewayTextBox.Text = adapter.Gateway;

        DnsPreset preset = adapter.DnsFromDhcp
            ? NetworkAdapterConfigService.DnsPresets[0]
            : NetworkAdapterConfigService.DnsPresets.FirstOrDefault(candidate =>
                  candidate.Primary is not null && adapter.DnsServers.FirstOrDefault() == candidate.Primary)
              ?? NetworkAdapterConfigService.DnsPresets.First(candidate => candidate.IsCustom);
        DnsPresetComboBox.SelectedItem = preset;
        if (!adapter.DnsFromDhcp)
        {
            // Show the servers actually in use, even when only one of them matches a known service.
            PrimaryDnsTextBox.Text = adapter.DnsServers.ElementAtOrDefault(0) ?? string.Empty;
            SecondaryDnsTextBox.Text = adapter.DnsServers.ElementAtOrDefault(1) ?? string.Empty;
        }

        UpdateAdapterFieldStates();
        _ = RefreshAdapterDetailsAsync(adapter);
        _ = TestDnsSpeedAsync();
    }

    /// <summary>Type, speed, MAC, DHCP server/lease, IPv6 and gateway/internet ping for the selected adapter.</summary>
    private async Task RefreshAdapterDetailsAsync(NetworkAdapterInfo adapter)
    {
        AdapterTypeSpeedText.Text = $"{adapter.AdapterType}  •  {NetworkAdapterConfigService.FormatLinkSpeed(adapter.LinkSpeedBitsPerSecond)}";
        AdapterMacText.Text = string.IsNullOrEmpty(adapter.MacAddress) ? "Not reported" : adapter.MacAddress;
        AdapterDhcpServerText.Text = !adapter.DhcpEnabled ? "Not used (static IP)"
            : string.IsNullOrEmpty(adapter.DhcpServer) ? "Not reported" : adapter.DhcpServer;
        AdapterIpv6Text.Text = adapter.IPv6Addresses is { Count: > 0 } ipv6 ? string.Join(", ", ipv6) : "None";
        AdapterLeaseText.Text = adapter.DhcpEnabled ? "Reading..." : "Not used (static IP)";
        AdapterGatewayPingText.Text = string.IsNullOrEmpty(adapter.Gateway) ? "No gateway" : "Measuring...";
        AdapterInternetPingText.Text = adapter.Status == "Connected" ? "Measuring..." : "Not connected";

        if (adapter.DhcpEnabled)
        {
            (string obtained, string expires) = await Task.Run(() => _adapterConfigService.GetDhcpLease(adapter.Id));
            if (IsSelectedAdapter(adapter))
            {
                AdapterLeaseText.Text = string.IsNullOrEmpty(obtained) ? "Not reported" : $"Obtained {obtained}  •  expires {expires}";
            }
        }

        Task<double?> gatewayPing = string.IsNullOrEmpty(adapter.Gateway) ? Task.FromResult<double?>(null) : NetworkAdapterConfigService.PingAsync(adapter.Gateway);
        Task<double?> internetPing = adapter.Status == "Connected" ? NetworkAdapterConfigService.PingAsync("1.1.1.1") : Task.FromResult<double?>(null);
        await Task.WhenAll(gatewayPing, internetPing);
        if (!IsSelectedAdapter(adapter))
        {
            return;
        }

        if (!string.IsNullOrEmpty(adapter.Gateway))
        {
            AdapterGatewayPingText.Text = $"{adapter.Gateway}  •  {NetworkAdapterConfigService.FormatPing(gatewayPing.Result)}";
        }

        if (adapter.Status == "Connected")
        {
            AdapterInternetPingText.Text = NetworkAdapterConfigService.FormatPing(internetPing.Result);
        }
    }

    private bool IsSelectedAdapter(NetworkAdapterInfo adapter) =>
        NetworkAdapterComboBox.SelectedItem is NetworkAdapterInfo selected && selected.Name == adapter.Name;

    /// <summary>One row of the DNS response-time list.</summary>
    private sealed class DnsLatencyRow
    {
        public string Name { get; init; } = string.Empty;
        public string? Primary { get; init; }
        public string? Secondary { get; init; }
        public DnsPreset? Preset { get; init; }
        public bool IsCurrent { get; init; }
        public double? PrimaryMs { get; set; }
        public double? SecondaryMs { get; set; }
        public bool IsFastest { get; set; }

        public string Servers => string.Join(", ", new[] { Primary, Secondary }.Where(server => !string.IsNullOrEmpty(server)));

        public double? Best => new[] { PrimaryMs, SecondaryMs }.Where(value => value is not null).Min();

        public string LatencyText
        {
            get
            {
                string text = string.IsNullOrEmpty(Secondary)
                    ? NetworkAdapterConfigService.FormatPing(PrimaryMs)
                    : $"{NetworkAdapterConfigService.FormatPing(PrimaryMs)}  /  {NetworkAdapterConfigService.FormatPing(SecondaryMs)}";
                return IsFastest ? $"{text}  ★ fastest" : text;
            }
        }

        public System.Windows.Media.Brush LatencyBrush => Best switch
        {
            null => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["DangerBrush"],
            < 40 => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["SuccessBrush"],
            < 100 => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["WarningBrush"],
            _ => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["DangerBrush"]
        };

        public Visibility UseVisibility => IsCurrent ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool _testingDnsSpeed;

    private async void TestDnsSpeedButton_Click(object sender, RoutedEventArgs e) => await TestDnsSpeedAsync();

    /// <summary>Pings the DNS servers currently in use and every preset, then lists them fastest first.</summary>
    private async Task TestDnsSpeedAsync()
    {
        if (_testingDnsSpeed)
        {
            return;
        }

        _testingDnsSpeed = true;
        TestDnsSpeedButton.IsEnabled = false;
        DnsLatencyHintText.Text = "Measuring...";
        try
        {
            List<DnsLatencyRow> rows = new();
            if (NetworkAdapterComboBox.SelectedItem is NetworkAdapterInfo adapter && adapter.DnsServers.Count > 0)
            {
                rows.Add(new DnsLatencyRow
                {
                    Name = adapter.DnsFromDhcp ? "Current DNS (automatic)" : "Current DNS (manual)",
                    Primary = adapter.DnsServers.ElementAtOrDefault(0),
                    Secondary = adapter.DnsServers.ElementAtOrDefault(1),
                    IsCurrent = true
                });
            }

            rows.AddRange(NetworkAdapterConfigService.DnsPresets
                .Where(preset => preset.Primary is not null)
                .Select(preset => new DnsLatencyRow { Name = preset.Name, Primary = preset.Primary, Secondary = preset.Secondary, Preset = preset }));

            await Task.WhenAll(rows.Select(async row =>
            {
                Task<double?> primary = NetworkAdapterConfigService.PingAsync(row.Primary ?? string.Empty);
                Task<double?> secondary = string.IsNullOrEmpty(row.Secondary) ? Task.FromResult<double?>(null) : NetworkAdapterConfigService.PingAsync(row.Secondary);
                row.PrimaryMs = await primary;
                row.SecondaryMs = await secondary;
            }));

            DnsLatencyRow? fastest = rows.Where(row => !row.IsCurrent && row.Best is not null).MinBy(row => row.Best);
            if (fastest is not null)
            {
                fastest.IsFastest = true;
            }

            DnsLatencyList.ItemsSource = rows
                .OrderByDescending(row => row.IsCurrent)
                .ThenBy(row => row.Best ?? double.MaxValue)
                .ToList();

            DnsLatencyRow? current = rows.FirstOrDefault(row => row.IsCurrent);
            if (current is not null && NetworkAdapterComboBox.SelectedItem is NetworkAdapterInfo selected)
            {
                NetworkAdapterCurrentText.Text = $"{selected.Summary}  •  DNS ping {current.LatencyText}";
            }

            DnsLatencyHintText.Text = $"Average of 3 pings to each server, measured {DateTime.Now:t}. Lower is faster; use the fastest that answers reliably.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("DNS speed test", exception);
            DnsLatencyHintText.Text = "The DNS speed test could not run.";
        }
        finally
        {
            _testingDnsSpeed = false;
            TestDnsSpeedButton.IsEnabled = true;
        }
    }

    private void UseDnsLatencyRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DnsLatencyRow row })
        {
            return;
        }

        DnsPresetComboBox.SelectedItem = row.Preset ?? NetworkAdapterConfigService.DnsPresets.First(preset => preset.IsCustom);
        PrimaryDnsTextBox.Text = row.Primary ?? string.Empty;
        SecondaryDnsTextBox.Text = row.Secondary ?? string.Empty;
        StatusText.Text = $"DNS fields set to {row.Name}. Choose Apply settings to use it.";
    }

    private System.Windows.Threading.DispatcherTimer? _dnsPingDebounce;

    /// <summary>Pings whatever is typed in the DNS fields, shortly after typing stops.</summary>
    private void DnsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_dnsPingDebounce is null)
        {
            _dnsPingDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _dnsPingDebounce.Tick += async (_, _) =>
            {
                _dnsPingDebounce.Stop();
                await PingDnsFieldAsync(PrimaryDnsTextBox, PrimaryDnsPingText);
                await PingDnsFieldAsync(SecondaryDnsTextBox, SecondaryDnsPingText);
            };
        }

        _dnsPingDebounce.Stop();
        _dnsPingDebounce.Start();
    }

    private static async Task PingDnsFieldAsync(System.Windows.Controls.TextBox box, TextBlock result)
    {
        string address = box.Text.Trim();
        if (!System.Net.IPAddress.TryParse(address, out _) || address.Count(character => character == '.') != 3)
        {
            result.Text = string.Empty;
            return;
        }

        result.Text = "...";
        double? milliseconds = await NetworkAdapterConfigService.PingAsync(address);
        if (box.Text.Trim() == address)
        {
            result.Text = NetworkAdapterConfigService.FormatPing(milliseconds);
        }
    }

    private void AdapterIpMode_Changed(object sender, RoutedEventArgs e) => UpdateAdapterFieldStates();

    private void DnsPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DnsPresetComboBox.SelectedItem is DnsPreset { Primary: not null } preset)
        {
            PrimaryDnsTextBox.Text = preset.Primary;
            SecondaryDnsTextBox.Text = preset.Secondary ?? string.Empty;
        }

        UpdateAdapterFieldStates();
    }

    private void UpdateAdapterFieldStates()
    {
        if (AdapterStaticFields is null || DnsServerFields is null)
        {
            return; // Raised while InitializeComponent is still creating controls.
        }

        AdapterStaticFields.IsEnabled = AdapterStaticRadio.IsChecked == true;
        DnsPreset? preset = DnsPresetComboBox.SelectedItem as DnsPreset;
        // Editable for every service, so one server can be changed while keeping the other.
        DnsServerFields.IsEnabled = preset?.IsAutomatic != true;
        DnsServerFields.Visibility = preset?.IsAutomatic == true ? Visibility.Collapsed : Visibility.Visible;
        DnsPresetNoteText.Text = preset is null ? string.Empty
            : preset.IsAutomatic ? preset.Note
            : $"{preset.Note} You can change either address below.";
    }

    private async void ResetAdapterToAutomaticButton_Click(object sender, RoutedEventArgs e)
    {
        AdapterDhcpRadio.IsChecked = true;
        DnsPresetComboBox.SelectedItem = NetworkAdapterConfigService.DnsPresets[0];
        await ApplyAdapterSettingsAsync();
    }

    private async void ApplyAdapterSettingsButton_Click(object sender, RoutedEventArgs e) => await ApplyAdapterSettingsAsync();

    private async Task ApplyAdapterSettingsAsync()
    {
        if (_applyingAdapterSettings || NetworkAdapterComboBox.SelectedItem is not NetworkAdapterInfo adapter
            || DnsPresetComboBox.SelectedItem is not DnsPreset dns)
        {
            return;
        }

        bool useDhcp = AdapterDhcpRadio.IsChecked == true;
        IReadOnlyList<string> problems = NetworkAdapterConfigService.Validate(
            useDhcp, AdapterIpTextBox.Text, AdapterMaskTextBox.Text, AdapterGatewayTextBox.Text,
            dns, PrimaryDnsTextBox.Text, SecondaryDnsTextBox.Text);
        if (problems.Count > 0)
        {
            WpfMessageBox.Show(this, string.Join("\n", problems), "Network adapter settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string ipPlan = useDhcp
            ? "IP address: automatic (DHCP)"
            : $"IP address: {AdapterIpTextBox.Text.Trim()} / {AdapterMaskTextBox.Text.Trim()}, gateway {(string.IsNullOrWhiteSpace(AdapterGatewayTextBox.Text) ? "none" : AdapterGatewayTextBox.Text.Trim())}";
        string dnsPlan = dns.IsAutomatic
            ? "DNS: automatic"
            : $"DNS: {PrimaryDnsTextBox.Text.Trim()}{(string.IsNullOrWhiteSpace(SecondaryDnsTextBox.Text) ? string.Empty : ", " + SecondaryDnsTextBox.Text.Trim())}";
        string remoteWarning = IsRemoteInteractiveSession()
            ? "\n\nREMOTE SESSION DETECTED: a wrong address or gateway can disconnect this RDP/remote session and you may lose access to this PC."
            : string.Empty;

        if (WpfMessageBox.Show(this,
                $"Change \"{adapter.Name}\" to:\n\n{ipPlan}\n{dnsPlan}\n\nThe connection can drop for a few seconds while Windows applies the change.{remoteWarning}\n\nApply these settings?",
                "Network adapter settings", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _applyingAdapterSettings = true;
        ApplyAdapterSettingsButton.IsEnabled = false;
        StatusText.Text = $"Applying network settings to {adapter.Name}...";
        try
        {
            string result = await _adapterConfigService.ApplyAsync(
                adapter, useDhcp, AdapterIpTextBox.Text, AdapterMaskTextBox.Text, AdapterGatewayTextBox.Text,
                dns.IsAutomatic, PrimaryDnsTextBox.Text, SecondaryDnsTextBox.Text);
            ApplicationLogService.WriteMessage("Network adapter settings", result);
            StatusText.Text = $"Applied. {result}";

            // DHCP can take a few seconds to hand out an address.
            await Task.Delay(useDhcp ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(1));
            RefreshNetworkAdapters();
            _ = RefreshSystemHealthDetailsAsync(forcePublicRefresh: true);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Network adapter settings", exception);
            StatusText.Text = "Network settings were not applied.";
            WpfMessageBox.Show(this, $"Windows could not apply the settings to \"{adapter.Name}\".\n\n{exception.Message}",
                "Network adapter settings", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshNetworkAdapters();
        }
        finally
        {
            _applyingAdapterSettings = false;
            ApplyAdapterSettingsButton.IsEnabled = true;
        }
    }
}
