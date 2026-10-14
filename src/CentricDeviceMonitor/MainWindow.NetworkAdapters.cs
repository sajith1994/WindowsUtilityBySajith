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
        if (preset.IsCustom)
        {
            PrimaryDnsTextBox.Text = adapter.DnsServers.ElementAtOrDefault(0) ?? string.Empty;
            SecondaryDnsTextBox.Text = adapter.DnsServers.ElementAtOrDefault(1) ?? string.Empty;
        }

        UpdateAdapterFieldStates();
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
        DnsServerFields.IsEnabled = preset?.IsCustom == true;
        DnsServerFields.Visibility = preset?.IsAutomatic == true ? Visibility.Collapsed : Visibility.Visible;
        DnsPresetNoteText.Text = preset?.Note ?? string.Empty;
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
