using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor.Dialogs;

public partial class IpScannerWindow : Window
{
    private readonly LocalIpScannerService _scannerService = new();
    private readonly DeviceStorageService _deviceStorageService = new();
    private readonly NetworkDeviceService _networkDeviceService = new();
    private readonly DeviceLogService _deviceLogService = new();
    private readonly TcpPortScannerService _portScannerService = new();
    private LocalSubnetInfo? _subnet;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _portScanCancellation;
    private bool _managedRefreshRunning;
    private bool _portScanRunning;

    public ObservableCollection<IpScanResult> Results { get; } = new();
    public ObservableCollection<DeviceEntry> ManagedDevices { get; } = new();
    public bool ManagedDevicesChanged { get; private set; }

    public IpScannerWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += IpScannerWindow_Loaded;
        Closing += (_, _) =>
        {
            _scanCancellation?.Cancel();
            _portScanCancellation?.Cancel();
        };
    }

    private async void IpScannerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await ReloadManagedDevicesAsync();
        if (ManagedDevices.Count > 0)
        {
            await RefreshAllManagedDevicesAsync(showCompletionMessage: false);
        }

        _subnet = _scannerService.GetLocalSubnet();
        if (_subnet is null)
        {
            SubnetText.Text = "No active Ethernet or Wi-Fi IPv4 subnet was detected.";
            ScanButton.IsEnabled = false;
            StatusText.Text = "Connect this PC to the local network before scanning.";
            return;
        }

        SubnetText.Text = $"Adapter: {_subnet.AdapterName} | PC: {_subnet.LocalAddress} | Scan range: {_subnet.Cidr}";
        StatusText.Text = $"Ready to scan {_subnet.Addresses.Count} local address(es). Use Ctrl or Shift to select multiple results.";
    }

    private async Task ReloadManagedDevicesAsync()
    {
        IReadOnlyList<DeviceEntry> saved = await _deviceStorageService.LoadAsync();
        ManagedDevices.Clear();
        foreach (DeviceEntry device in saved.OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase))
        {
            ManagedDevices.Add(device);
        }
        UpdateManagedCount();
    }

    private void UpdateManagedCount() => ManagedCountText.Text = $"{ManagedDevices.Count} managed";

    private async Task SaveManagedDevicesAsync()
    {
        await _deviceStorageService.SaveAsync(ManagedDevices);
        ManagedDevicesChanged = true;
        UpdateManagedCount();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_subnet is null || _scanCancellation is not null)
        {
            return;
        }

        Results.Clear();
        AddSelectedButton.IsEnabled = false;
        ScanProgressBar.Value = 0;
        ProgressText.Text = $"0/{_subnet.Addresses.Count}";
        StatusText.Text = "Scanning local network. The window remains usable while the scan runs.";
        ScanButton.IsEnabled = false;
        CancelScanButton.IsEnabled = true;
        _scanCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _scanCancellation.Token;

        DateTime lastProgressUpdate = DateTime.MinValue;
        Progress<IpScanProgress> progress = new(update =>
        {
            if (!IsLoaded)
            {
                return;
            }

            if (update.Result is not null &&
                !Results.Any(existing => string.Equals(existing.IpAddress, update.Result.IpAddress, StringComparison.OrdinalIgnoreCase)))
            {
                Results.Add(update.Result);
                StatusText.Text = $"{update.Stage}. Found {Results.Count} device(s).";
            }

            DateTime now = DateTime.UtcNow;
            if (update.Result is not null || update.Completed >= update.Total || now - lastProgressUpdate >= TimeSpan.FromMilliseconds(120))
            {
                ScanProgressBar.Value = update.Total == 0 ? 0 : update.Completed * 100d / update.Total;
                ProgressText.Text = update.Completed >= update.Total ? update.Stage : $"{update.Completed}/{update.Total}";
                lastProgressUpdate = now;
            }
        });

        try
        {
            await Task.Run(
                async () => await _scannerService.ScanAsync(_subnet, progress, cancellationToken).ConfigureAwait(false),
                cancellationToken);

            ScanProgressBar.Value = 100;
            ProgressText.Text = "Complete";
            StatusText.Text = $"Scan complete. {Results.Count} device(s) found.";
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Cancelled";
            StatusText.Text = $"Scan cancelled. {Results.Count} device(s) found so far.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Local IP scanner", exception);
            ProgressText.Text = "Failed";
            StatusText.Text = $"Scan failed: {exception.Message}";
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            ScanButton.IsEnabled = _subnet is not null;
            CancelScanButton.IsEnabled = false;
        }
    }

    private void CancelScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is null)
        {
            return;
        }

        StatusText.Text = "Cancelling scan...";
        CancelScanButton.IsEnabled = false;
        _scanCancellation.Cancel();
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int selectedCount = ResultsGrid.SelectedItems.Count;
        AddSelectedButton.IsEnabled = selectedCount > 0;
        ScanPortsButton.IsEnabled = selectedCount > 0 && !_portScanRunning;
        AddSelectedButton.Content = selectedCount <= 1
            ? "Add selected to managed devices"
            : $"Add {selectedCount} selected to managed devices";
        ScanPortsButton.Content = selectedCount <= 1
            ? "Check open ports"
            : $"Check ports on {selectedCount} devices";
    }

    private async void ScanPortsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_portScanRunning)
        {
            return;
        }

        List<IpScanResult> selected = ResultsGrid.SelectedItems.Cast<IpScanResult>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        _portScanRunning = true;
        ScanPortsButton.IsEnabled = false;
        _portScanCancellation?.Dispose();
        _portScanCancellation = new CancellationTokenSource();

        try
        {
            int completed = 0;
            foreach (IpScanResult device in selected)
            {
                _portScanCancellation.Token.ThrowIfCancellationRequested();
                device.OpenPortsDisplay = "Scanning...";
                ResultsGrid.Items.Refresh();
                StatusText.Text = $"Checking {_portScannerService.PortCount} common TCP ports on {device.IpAddress} ({completed + 1}/{selected.Count})...";

                IReadOnlyList<TcpPortScanResult> openPorts = await _portScannerService.ScanCommonPortsAsync(
                    device.IpAddress,
                    _portScanCancellation.Token);

                device.OpenPortsDisplay = openPorts.Count == 0
                    ? "None found (common ports)"
                    : string.Join(", ", openPorts.Select(port => port.Display));
                completed++;
                ResultsGrid.Items.Refresh();
            }

            StatusText.Text = $"Open-port check complete for {completed} device(s). Scanned {_portScannerService.PortCount} common TCP ports per device.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Open-port check cancelled.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("IP scanner open-port check", exception);
            StatusText.Text = $"Open-port check failed: {exception.Message}";
        }
        finally
        {
            _portScanRunning = false;
            _portScanCancellation?.Dispose();
            _portScanCancellation = null;
            ScanPortsButton.IsEnabled = ResultsGrid.SelectedItems.Count > 0;
        }
    }

    private async void AddSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        List<IpScanResult> selected = ResultsGrid.SelectedItems
            .Cast<IpScanResult>()
            .OrderBy(result => IPAddress.Parse(result.IpAddress).GetAddressBytes(), ByteArrayComparer.Instance)
            .ToList();

        if (selected.Count == 0)
        {
            return;
        }

        int added = 0;
        int skipped = 0;
        foreach (IpScanResult candidate in selected)
        {
            if (ManagedDevices.Any(device => string.Equals(device.IpAddress, candidate.IpAddress, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            ManagedDevices.Add(new DeviceEntry
            {
                Id = Guid.NewGuid(),
                Name = candidate.SuggestedName,
                IpAddress = candidate.IpAddress,
                Hostname = candidate.Hostname,
                MacAddress = candidate.MacAddress,
                RoundTripTime = candidate.RoundTripTime,
                LastChecked = DateTime.Now,
                Status = "Online"
            });
            added++;
        }

        if (added > 0)
        {
            await SaveManagedDevicesAsync();
        }

        StatusText.Text = skipped > 0
            ? $"Added {added} device(s); skipped {skipped} already-managed IP address(es)."
            : $"Added {added} device(s) to managed monitoring.";
        ManagedStatusText.Text = StatusText.Text;
    }

    private void OpenManagedTabButton_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;

    private async void AddManagedDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        DeviceEditorWindow editor = new() { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        if (ManagedDevices.Any(device => string.Equals(device.IpAddress, editor.IpAddress, StringComparison.OrdinalIgnoreCase)))
        {
            WpfMessageBox.Show("That IP address is already in the managed list.", "Managed devices", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DeviceEntry device = new()
        {
            Id = Guid.NewGuid(),
            Name = editor.DeviceName,
            IpAddress = editor.IpAddress,
            Status = "Not checked"
        };
        ManagedDevices.Add(device);
        await RefreshDeviceAsync(device);
        await SaveManagedDevicesAsync();
        ManagedGrid.SelectedItem = device;
        ManagedStatusText.Text = $"Added {device.Name} ({device.IpAddress}).";
    }

    private async void EditManagedDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        DeviceEntry? selected = ManagedGrid.SelectedItem as DeviceEntry;
        if (selected is null)
        {
            WpfMessageBox.Show("Select a managed device first.", "Managed devices", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DeviceEditorWindow editor = new(selected) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        bool duplicate = ManagedDevices.Any(device => device.Id != selected.Id && string.Equals(device.IpAddress, editor.IpAddress, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            WpfMessageBox.Show("Another managed device already uses that IP address.", "Managed devices", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        selected.Name = editor.DeviceName;
        selected.IpAddress = editor.IpAddress;
        selected.Hostname = "Not available";
        selected.MacAddress = "Not available";
        selected.Status = "Not checked";
        selected.RoundTripTime = null;
        selected.LastChecked = null;
        selected.LastError = string.Empty;
        await RefreshDeviceAsync(selected);
        await SaveManagedDevicesAsync();
        ManagedStatusText.Text = $"Updated {selected.Name}.";
    }

    private async void RemoveManagedDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        DeviceEntry? selected = ManagedGrid.SelectedItem as DeviceEntry;
        if (selected is null)
        {
            WpfMessageBox.Show("Select a managed device first.", "Managed devices", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBoxResult answer = WpfMessageBox.Show(
            $"Remove {selected.Name} ({selected.IpAddress}) from monitoring?\n\nExisting connection/restart history is kept unless you delete it separately.",
            "Remove managed device",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        ManagedDevices.Remove(selected);
        await SaveManagedDevicesAsync();
        ManagedStatusText.Text = "Managed device removed.";
    }

    private void ViewManagedDeviceLogButton_Click(object sender, RoutedEventArgs e)
    {
        DeviceEntry? selected = ManagedGrid.SelectedItem as DeviceEntry;
        if (selected is null)
        {
            WpfMessageBox.Show("Select a managed device first.", "Managed devices", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DeviceLogWindow logWindow = new(selected, _deviceLogService) { Owner = this };
        logWindow.ShowDialog();
    }

    private async void RefreshManagedSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        DeviceEntry? selected = ManagedGrid.SelectedItem as DeviceEntry;
        if (selected is null)
        {
            WpfMessageBox.Show("Select a managed device first.", "Managed devices", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RefreshDeviceAsync(selected);
        await SaveManagedDevicesAsync();
        ManagedStatusText.Text = $"Refreshed {selected.Name}: {selected.Status}.";
    }

    private async void RefreshManagedAllButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAllManagedDevicesAsync(showCompletionMessage: true);
    }

    private async Task RefreshAllManagedDevicesAsync(bool showCompletionMessage)
    {
        if (_managedRefreshRunning || ManagedDevices.Count == 0)
        {
            return;
        }

        _managedRefreshRunning = true;
        ManagedStatusText.Text = $"Refreshing {ManagedDevices.Count} managed device(s)...";
        try
        {
            using SemaphoreSlim limiter = new(8, 8);
            await Task.WhenAll(ManagedDevices.Select(async device =>
            {
                await limiter.WaitAsync();
                try
                {
                    await RefreshDeviceAsync(device);
                }
                finally
                {
                    limiter.Release();
                }
            }));

            if (showCompletionMessage)
            {
                ManagedStatusText.Text = $"Refreshed {ManagedDevices.Count} managed device(s).";
            }
            else
            {
                int online = ManagedDevices.Count(device => string.Equals(device.Status, "Online", StringComparison.OrdinalIgnoreCase));
                ManagedStatusText.Text = $"Live status: {online} of {ManagedDevices.Count} online.";
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Managed device refresh", exception);
            ManagedStatusText.Text = $"Refresh failed: {exception.Message}";
        }
        finally
        {
            _managedRefreshRunning = false;
        }
    }

    private async Task RefreshDeviceAsync(DeviceEntry device)
    {
        device.IsChecking = true;
        try
        {
            NetworkCheckResult result = await _networkDeviceService.CheckAsync(device.IpAddress);
            device.Hostname = result.Hostname;
            device.MacAddress = result.MacAddress;
            device.Status = result.IsOnline ? "Online" : "Offline";
            device.RoundTripTime = result.RoundTripTime;
            device.LastChecked = DateTime.Now;
            device.LastError = result.ErrorMessage;
        }
        finally
        {
            device.IsChecking = false;
        }
    }

    private void ManagedGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ManagedGrid.SelectedItem is DeviceEntry)
        {
            EditManagedDeviceButton_Click(sender, e);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            int length = Math.Min(x.Length, y.Length);
            for (int index = 0; index < length; index++)
            {
                int comparison = x[index].CompareTo(y[index]);
                if (comparison != 0) return comparison;
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
