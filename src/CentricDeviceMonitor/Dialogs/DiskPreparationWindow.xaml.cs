using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor.Dialogs;

public partial class DiskPreparationWindow : Window
{
    private readonly DiskPreparationService _diskService = new();
    private readonly WindowsUtilityService _windowsUtilityService = new();
    private bool _busy;
    private bool _refreshingDisks;

    public ObservableCollection<DiskPreparationInfo> Disks { get; } = new();
    public ObservableCollection<DiskPartitionInfo> Partitions { get; } = new();

    private DiskPreparationInfo? SelectedDisk => DiskGrid.SelectedItem as DiskPreparationInfo;
    private DiskPartitionInfo? SelectedPartition => PartitionGrid.SelectedItem as DiskPartitionInfo;

    public DiskPreparationWindow()
    {
        InitializeComponent();
        DataContext = this;
        UpdatePartitionSizeMode();
        Loaded += async (_, _) => await RefreshDisksAsync();
    }

    private async Task RefreshDisksAsync()
    {
        if (_busy)
        {
            return;
        }

        _refreshingDisks = true;
        try
        {
            OperationStatusText.Text = "Refreshing physical disks...";
            int? selectedNumber = SelectedDisk?.Number;
            int? selectedPartitionNumber = SelectedPartition?.PartitionNumber;
            IReadOnlyList<DiskPreparationInfo> disks = await _diskService.GetDisksAsync();
            Disks.Clear();
            foreach (DiskPreparationInfo disk in disks)
            {
                Disks.Add(disk);
            }

            if (selectedNumber.HasValue)
            {
                DiskGrid.SelectedItem = Disks.FirstOrDefault(disk => disk.Number == selectedNumber.Value);
            }

            if (DiskGrid.SelectedItem is null && Disks.Count > 0)
            {
                DiskGrid.SelectedIndex = 0;
            }

            await RefreshPartitionsAsync(selectedPartitionNumber);
            UpdateSelectionDisplay();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Refresh disk preparation list", exception);
            OperationStatusText.Text = exception.Message;
            WpfMessageBox.Show(exception.Message, "Disk enumeration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _refreshingDisks = false;
        }
    }

    private async Task RefreshPartitionsAsync(int? preferredPartitionNumber = null)
    {
        Partitions.Clear();
        DiskPreparationInfo? disk = SelectedDisk;
        if (disk is null)
        {
            PartitionHeaderText.Text = "Partitions - select a disk above";
            return;
        }

        PartitionHeaderText.Text = $"Partitions on Disk {disk.Number} - {disk.FriendlyName}";
        try
        {
            IReadOnlyList<DiskPartitionInfo> partitions = await _diskService.GetPartitionsAsync(disk.Number);
            foreach (DiskPartitionInfo partition in partitions)
            {
                Partitions.Add(partition);
            }

            if (preferredPartitionNumber.HasValue)
            {
                PartitionGrid.SelectedItem = Partitions.FirstOrDefault(item => item.PartitionNumber == preferredPartitionNumber.Value);
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException($"Refresh partitions for disk {disk.Number}", exception);
            OperationStatusText.Text = exception.Message;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshDisksAsync();

    private void OpenDiskPartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _windowsUtilityService.OpenDiskPartElevated();
            OperationStatusText.Text = "DiskPart opened as Administrator. Click Refresh disks after making changes.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Open DiskPart", exception);
            WpfMessageBox.Show(exception.Message, "DiskPart", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DiskGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _refreshingDisks)
        {
            return;
        }

        await RefreshPartitionsAsync();
        UpdateSelectionDisplay();
    }

    private void PartitionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionDisplay();

    private void PartitionSizeMode_Changed(object sender, RoutedEventArgs e) => UpdatePartitionSizeMode();

    private void UpdatePartitionSizeMode()
    {
        if (PartitionSizeTextBox is null || UseMaximumSizeCheckBox is null)
        {
            return;
        }

        PartitionSizeTextBox.IsEnabled = UseMaximumSizeCheckBox.IsChecked != true;
    }

    private void UpdateSelectionDisplay()
    {
        DiskPreparationInfo? disk = SelectedDisk;
        DiskPartitionInfo? partition = SelectedPartition;
        if (disk is null)
        {
            SelectedDiskText.Text = "Select a physical disk above. Boot/system disks are always blocked.";
            SetActionButtons(false, false, false, false, false, false);
            return;
        }

        string state = disk.IsOffline ? "Offline" : "Online";
        if (disk.IsReadOnly)
        {
            state += " / Read-only";
        }

        SelectedDiskText.Text =
            $"Disk {disk.Number}: {disk.FriendlyName} • {disk.SizeText} • {disk.BusType} • {disk.PartitionStyle} • " +
            $"{disk.NumberOfPartitions} partition(s) • {disk.UnallocatedText} unallocated • largest free {disk.LargestFreeExtentText} • {state} • {disk.ProtectionText}";

        bool canModifyPartition = !_busy && !disk.IsProtected && partition is not null && !partition.IsProtected;
        SetActionButtons(
            !_busy && disk.CanClean,
            !_busy && disk.CanInitialize,
            !_busy && disk.CanCreatePartition,
            canModifyPartition,
            canModifyPartition,
            !_busy && !disk.IsProtected);

        if (disk.IsProtected)
        {
            OperationStatusText.Text = "Protected: Windows reports this as a boot/system disk. All destructive disk actions are disabled.";
        }
        else if (string.Equals(disk.PartitionStyle, "RAW", StringComparison.OrdinalIgnoreCase))
        {
            OperationStatusText.Text = "Disk is RAW. Choose GPT or MBR and click Initialize disk. Then create one or more partitions, or use Erase + prepare full disk for a one-click full-volume setup.";
        }
        else if (partition is not null)
        {
            OperationStatusText.Text =
                $"Selected {partition.DisplayText}. You can quick-format or delete this partition. " +
                $"Disk has {disk.UnallocatedText} unallocated space available for additional partitions.";
        }
        else if (disk.CanCreatePartition)
        {
            OperationStatusText.Text =
                $"Disk is initialized and has {disk.UnallocatedText} unallocated. Use maximum free space for one large volume, " +
                "or untick it and enter a GB size; repeat Create partition to build multiple partitions.";
        }
        else
        {
            OperationStatusText.Text =
                "The disk is initialized but Windows reports no usable unallocated space. Select a partition to format/delete it, or use Clean / Erase + prepare if you intentionally want to start over.";
        }
    }

    private void SetActionButtons(bool clean, bool initialize, bool create, bool format, bool delete, bool prepare)
    {
        CleanDiskButton.IsEnabled = clean;
        InitializeButton.IsEnabled = initialize;
        CreatePartitionButton.IsEnabled = create;
        FormatPartitionButton.IsEnabled = format;
        DeletePartitionButton.IsEnabled = delete;
        PrepareDiskButton.IsEnabled = prepare;
    }

    private async void CleanDiskButton_Click(object sender, RoutedEventArgs e)
    {
        DiskPreparationInfo? disk = SelectedDisk;
        if (disk is null || !disk.CanClean || _busy)
        {
            return;
        }

        if (!ConfirmWholeDiskDestruction(
                disk,
                "CLEAN DISK",
                "This removes ALL partitions and partition metadata. The disk becomes RAW/uninitialized."))
        {
            return;
        }

        await RunDiskOperationAsync("Cleaning the selected disk...", "Disk clean", () => _diskService.CleanDiskAsync(disk));
    }

    private async void InitializeButton_Click(object sender, RoutedEventArgs e)
    {
        DiskPreparationInfo? disk = SelectedDisk;
        if (disk is null || !disk.CanInitialize || _busy)
        {
            return;
        }

        string partitionStyle = GetSelectedComboText(PartitionStyleComboBox, "GPT");
        MessageBoxResult answer = WpfMessageBox.Show(
            $"INITIALIZE DISK {disk.Number}\n\nModel: {disk.FriendlyName}\nSize: {disk.SizeText}\nSerial: {DisplaySerial(disk)}\n\n" +
            $"Initialize this RAW disk as {partitionStyle}?\n\nThis only creates the partition table. You can create and format partitions afterwards.",
            "Confirm disk initialization",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunDiskOperationAsync(
            $"Initializing Disk {disk.Number} as {partitionStyle}...",
            "Disk initialized",
            () => _diskService.InitializeDiskAsync(disk, partitionStyle));
    }

    private async void CreatePartitionButton_Click(object sender, RoutedEventArgs e)
    {
        DiskPreparationInfo? disk = SelectedDisk;
        if (disk is null || !disk.CanCreatePartition || _busy)
        {
            return;
        }

        if (!TryGetRequestedPartitionSize(out ulong? sizeBytes, out string error))
        {
            WpfMessageBox.Show(error, "Partition size", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string fileSystem = GetSelectedComboText(FileSystemComboBox, "NTFS");
        string label = VolumeLabelTextBox.Text.Trim();
        string sizeText = sizeBytes.HasValue
            ? $"{sizeBytes.Value / (1024d * 1024d * 1024d):0.##} GB"
            : "maximum available free space";

        MessageBoxResult answer = WpfMessageBox.Show(
            $"CREATE PARTITION\n\nDisk: {disk.Number} - {disk.FriendlyName}\nAvailable: {disk.UnallocatedText}\n" +
            $"New partition: {sizeText}\nFile system: {fileSystem}\nLabel: {(string.IsNullOrWhiteSpace(label) ? "New Volume" : label)}\n\n" +
            "The new partition will be quick-formatted. Existing partitions are not deleted.",
            "Confirm partition creation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunDiskOperationAsync(
            "Creating and formatting the new partition...",
            "Partition created",
            () => _diskService.CreatePartitionAsync(disk, sizeBytes, fileSystem, label));
    }

    private async void FormatPartitionButton_Click(object sender, RoutedEventArgs e)
    {
        DiskPreparationInfo? disk = SelectedDisk;
        DiskPartitionInfo? partition = SelectedPartition;
        if (disk is null || partition is null || disk.IsProtected || partition.IsProtected || _busy)
        {
            return;
        }

        string fileSystem = GetSelectedComboText(FileSystemComboBox, "NTFS");
        string label = VolumeLabelTextBox.Text.Trim();
        MessageBoxResult answer = WpfMessageBox.Show(
            $"FORMAT PARTITION\n\nDisk: {disk.Number} - {disk.FriendlyName}\nPartition: {partition.PartitionNumber}\n" +
            $"Drive: {partition.DriveLetterText}\nSize: {partition.SizeText}\nNew file system: {fileSystem}\n" +
            $"New label: {(string.IsNullOrWhiteSpace(label) ? "New Volume" : label)}\n\n" +
            "ALL DATA ON THIS PARTITION WILL BE ERASED. Other partitions on the disk are not changed.",
            "Confirm partition format",
            MessageBoxButton.YesNo,
            MessageBoxImage.Stop);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunDiskOperationAsync(
            "Formatting the selected partition...",
            "Partition formatted",
            () => _diskService.FormatPartitionAsync(disk, partition, fileSystem, label));
    }

    private async void DeletePartitionButton_Click(object sender, RoutedEventArgs e)
    {
        DiskPreparationInfo? disk = SelectedDisk;
        DiskPartitionInfo? partition = SelectedPartition;
        if (disk is null || partition is null || disk.IsProtected || partition.IsProtected || _busy)
        {
            return;
        }

        MessageBoxResult answer = WpfMessageBox.Show(
            $"DELETE PARTITION\n\nDisk: {disk.Number} - {disk.FriendlyName}\nPartition: {partition.PartitionNumber}\nDrive: {partition.DriveLetterText}\nSize: {partition.SizeText}\nType: {partition.Type}\n\n" +
            "All files stored in this partition will become inaccessible. The space will become unallocated.\n\nContinue?",
            "Confirm partition deletion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Stop);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunDiskOperationAsync(
            "Deleting the selected partition...",
            "Partition deleted",
            () => _diskService.DeletePartitionAsync(disk, partition));
    }

    private async void PrepareDiskButton_Click(object sender, RoutedEventArgs e)
    {
        DiskPreparationInfo? disk = SelectedDisk;
        if (disk is null || disk.IsProtected || _busy)
        {
            return;
        }

        string partitionStyle = GetSelectedComboText(PartitionStyleComboBox, "GPT");
        string fileSystem = GetSelectedComboText(FileSystemComboBox, "NTFS");
        string label = VolumeLabelTextBox.Text.Trim();

        if (!ConfirmWholeDiskDestruction(
                disk,
                "ERASE + PREPARE FULL DISK",
                $"This erases the entire disk, initializes it as {partitionStyle}, creates one full-size partition and quick-formats it as {fileSystem}."))
        {
            return;
        }

        await RunDiskOperationAsync(
            "Erasing, initializing, partitioning and formatting the selected disk...",
            "Disk prepared",
            () => _diskService.EraseAndPrepareFullDiskAsync(disk, partitionStyle, fileSystem, label));
    }

    private bool ConfirmWholeDiskDestruction(DiskPreparationInfo disk, string actionTitle, string description)
    {
        MessageBoxResult first = WpfMessageBox.Show(
            $"{actionTitle}\n\nDisk {disk.Number}\nModel: {disk.FriendlyName}\nSize: {disk.SizeText}\nSerial: {DisplaySerial(disk)}\n\n" +
            $"{description}\n\nTHIS ACTION AFFECTS THE ENTIRE SELECTED DISK. Continue?",
            actionTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Stop);
        if (first != MessageBoxResult.Yes)
        {
            return false;
        }

        MessageBoxResult second = WpfMessageBox.Show(
            $"FINAL CONFIRMATION\n\nDisk {disk.Number} - {disk.FriendlyName} - {disk.SizeText}\nSerial: {DisplaySerial(disk)}\n\n" +
            "Click YES only if you are certain this is the correct data/removable disk.",
            "Confirm destructive disk operation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return second == MessageBoxResult.Yes;
    }

    private bool TryGetRequestedPartitionSize(out ulong? sizeBytes, out string error)
    {
        sizeBytes = null;
        error = string.Empty;
        if (UseMaximumSizeCheckBox.IsChecked == true)
        {
            return true;
        }

        string raw = PartitionSizeTextBox.Text.Trim().Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double sizeGb) ||
            double.IsNaN(sizeGb) || double.IsInfinity(sizeGb))
        {
            error = "Enter a valid partition size in GB, for example 100 or 250.5.";
            return false;
        }

        if (sizeGb < 0.05)
        {
            error = "Partition size must be at least 0.05 GB (about 51 MB).";
            return false;
        }

        double bytes = sizeGb * 1024d * 1024d * 1024d;
        if (bytes > ulong.MaxValue)
        {
            error = "The requested partition size is too large.";
            return false;
        }

        sizeBytes = checked((ulong)Math.Round(bytes));
        return true;
    }

    private async Task RunDiskOperationAsync(string progressText, string successTitle, Func<Task<string>> operation)
    {
        _busy = true;
        RefreshButton.IsEnabled = false;
        SetActionButtons(false, false, false, false, false, false);
        OperationStatusText.Text = progressText;
        try
        {
            string result = await operation();
            ApplicationLogService.WriteMessage("Disk operation", result);
            OperationStatusText.Text = result;
            WpfMessageBox.Show(result, successTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException(successTitle, exception);
            OperationStatusText.Text = exception.Message;
            WpfMessageBox.Show(exception.Message, "Disk operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
            await RefreshDisksAsync();
        }
    }

    private static string GetSelectedComboText(WpfComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private static string DisplaySerial(DiskPreparationInfo disk) =>
        string.IsNullOrWhiteSpace(disk.SerialNumber) ? "Not reported" : disk.SerialNumber;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
