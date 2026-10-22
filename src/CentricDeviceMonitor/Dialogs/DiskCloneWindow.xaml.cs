using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using CentricDeviceMonitor.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor.Dialogs;

/// <summary>
/// Clone a whole disk to a new SSD (Windows moves with it and starts from it), or copy one partition, with a plan of
/// every partition before anything starts, a typed confirmation of the disk that is erased, live progress and cancel.
/// Needs administrator rights (a standard user's dashboard opens it in an elevated window).
/// </summary>
public partial class DiskCloneWindow : Window
{
    private readonly DiskCloneService _service = new();
    private IReadOnlyList<CloneDisk> _disks = Array.Empty<CloneDisk>();
    private ClonePlan? _clonePlan;
    private ClonePlan? _partitionPlan;
    private BootSetupPlan? _bootPlan;
    private CancellationTokenSource? _running;

    public sealed record PlanRow(string Partition, string FileSystem, string Now, string Planned, string How);

    public sealed record PartitionChoice(CloneDisk Disk, ClonePartition Partition)
    {
        public string DisplayText => $"Disk {Disk.Number}  •  {Partition.Title}  •  {DiskClonePlanner.FormatSize(Partition.Size)} {Partition.FileSystem}"
            + (Partition.UsedBytes > 0 ? $" ({DiskClonePlanner.FormatSize(Partition.UsedBytes)} used)" : string.Empty)
            + (Partition.IsBoot ? "  •  Windows" : string.Empty);
    }

    private sealed record LetterChoice(string Text, char? Letter)
    {
        public override string ToString() => Text;
    }

    public DiskCloneWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadDisksAsync();
        Closing += Window_Closing;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadDisksAsync();

    private async Task LoadDisksAsync()
    {
        if (_running is not null)
        {
            return;
        }

        RefreshButton.IsEnabled = false;
        StatusLine.Text = "Reading the disks...";
        try
        {
            _disks = await _service.GetDisksAsync();
            int? source = (SourceDiskComboBox.SelectedItem as CloneDisk)?.Number;
            int? target = (TargetDiskComboBox.SelectedItem as CloneDisk)?.Number;
            SourceDiskComboBox.ItemsSource = _disks;
            TargetDiskComboBox.ItemsSource = _disks.Where(disk => !disk.IsBoot && !disk.IsSystem).ToList();
            SourceDiskComboBox.SelectedItem = _disks.FirstOrDefault(disk => disk.Number == source) ?? _disks.FirstOrDefault(disk => disk.HasWindows) ?? _disks.FirstOrDefault();
            TargetDiskComboBox.SelectedItem = ((IEnumerable<CloneDisk>)TargetDiskComboBox.ItemsSource).FirstOrDefault(disk => disk.Number == target)
                ?? ((IEnumerable<CloneDisk>)TargetDiskComboBox.ItemsSource).FirstOrDefault(disk => disk.Number != (SourceDiskComboBox.SelectedItem as CloneDisk)?.Number);

            PartitionSourceComboBox.ItemsSource = _disks.SelectMany(disk => disk.Partitions.Where(partition => !partition.IsMsr)
                .Select(partition => new PartitionChoice(disk, partition))).ToList();
            PartitionSourceComboBox.SelectedIndex = PartitionSourceComboBox.Items.Count > 0 ? 0 : -1;
            PartitionTargetDiskComboBox.ItemsSource = _disks;
            PartitionTargetDiskComboBox.SelectedItem = TargetDiskComboBox.SelectedItem ?? _disks.FirstOrDefault();
            int? bootDisk = (BootDiskComboBox.SelectedItem as CloneDisk)?.Number;
            List<CloneDisk> bootDisks = _disks.Where(disk => !disk.IsBoot && !disk.IsSystem && disk.Style is "GPT" or "MBR").ToList();
            BootDiskComboBox.ItemsSource = bootDisks;
            BootDiskComboBox.SelectedItem = bootDisks.FirstOrDefault(disk => disk.Number == bootDisk) ?? bootDisks.FirstOrDefault();

            List<LetterChoice> letters = new() { new("Next free letter", null), new("No letter", '-') };
            HashSet<char> used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
            letters.AddRange(Enumerable.Range('D', 'Z' - 'D' + 1).Select(code => (char)code).Where(letter => !used.Contains(letter)).Select(letter => new LetterChoice($"{letter}:", letter)));
            LetterComboBox.ItemsSource = letters;
            LetterComboBox.SelectedIndex = 0;
            StatusLine.Text = $"{_disks.Count} disk{(_disks.Count == 1 ? "" : "s")} found. Tip: do the first clone with a spare disk, and keep the old disk until the new one starts Windows.";
            UpdateClonePlan();
            UpdatePartitionPlan();
            UpdateBootPlan();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException
            or Win32Exception or IOException)
        {
            ApplicationLogService.WriteException("Disk clone: reading disks", exception);
            StatusLine.Text = $"The disks could not be read: {exception.Message}";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    // ---- Whole disk ----------------------------------------------------------------------------------------

    private void CloneSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateClonePlan();

    private void CloneOption_Changed(object sender, RoutedEventArgs e) => UpdateClonePlan();

    private void ConfirmDiskBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateStartButtons();

    private void UpdateClonePlan()
    {
        if (!IsLoaded)
        {
            return; // Raised while the window is being built (ticked boxes), before every control exists.
        }

        if (SourceDiskComboBox.SelectedItem is not CloneDisk source || TargetDiskComboBox.SelectedItem is not CloneDisk target)
        {
            _clonePlan = null;
            PlanGrid.ItemsSource = null;
            PlanProblemsText.Visibility = Visibility.Collapsed;
            PlanNotesText.Text = TargetDiskComboBox.Items.Count == 0
                ? "No other disk is connected. Connect the new SSD (in a second slot, or with a USB adapter or enclosure), then select Refresh disks."
                : "Choose the disk to copy from and the disk to copy to.";
            UpdateStartButtons();
            return;
        }

        _clonePlan = DiskClonePlanner.PlanClone(source, target);
        bool usedOnly = UsedOnlyCheck.IsChecked == true;
        PlanGrid.ItemsSource = _clonePlan.Partitions.Select(planned => new PlanRow(
            planned.Source.Title,
            planned.Source.FileSystem.Length > 0 ? planned.Source.FileSystem : "-",
            DiskClonePlanner.FormatSize(planned.Source.Size),
            DiskClonePlanner.FormatSize(planned.Size) + (planned.Grows ? "  (grows)" : planned.Shrinks ? "  (shrunk first)" : string.Empty),
            Describe(planned, usedOnly))).ToList();
        PlanProblemsText.Text = string.Join("\n", _clonePlan.Problems);
        PlanProblemsText.Visibility = _clonePlan.Problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        long total = _clonePlan.Partitions.Sum(planned => (long)(usedOnly ? planned.BytesToCopy : planned.Mode == CopyMode.Skip ? 0 : planned.Source.Size));
        PlanNotesText.Text = string.Join("\n", _clonePlan.Notes.Append(
            $"About {DiskClonePlanner.FormatSize((ulong)total)} is copied. Everything on Disk {target.Number} ({target.Name}, {DiskClonePlanner.FormatSize(target.Size)}) is erased first."));
        BootableCheck.IsEnabled = source.HasWindows;
        BootableCheck.IsChecked = source.HasWindows && BootableCheck.IsChecked == true;
        ConfirmPromptText.Text = $"To start, type {target.Number} (the number of the disk that will be erased):";
        UpdateStartButtons();
    }

    private static string Describe(PlannedPartition planned, bool usedOnly) => planned.Mode switch
    {
        CopyMode.Skip => "Left empty (holds nothing)",
        CopyMode.SnapshotUsed when usedOnly => $"Snapshot, used space ({DiskClonePlanner.FormatSize(planned.Source.UsedBytes)})",
        CopyMode.SnapshotUsed or CopyMode.SnapshotFull => "Snapshot, every sector",
        _ => "Every sector"
    };

    private void UpdateStartButtons()
    {
        bool idle = _running is null;
        StartCloneButton.IsEnabled = idle && _clonePlan?.CanStart == true && TargetDiskComboBox.SelectedItem is CloneDisk target
            && ConfirmDiskBox.Text.Trim() == target.Number.ToString();
        StartPartitionButton.IsEnabled = idle && _partitionPlan?.CanStart == true;
        MakeBootableButton.IsEnabled = idle && _bootPlan?.CanStart == true;
    }

    private async void StartCloneButton_Click(object sender, RoutedEventArgs e)
    {
        if (_clonePlan is not { CanStart: true } plan || SourceDiskComboBox.SelectedItem is not CloneDisk source || TargetDiskComboBox.SelectedItem is not CloneDisk target)
        {
            return;
        }

        string shrink = plan.ShrinkFirst is { } first ? $"\n\n{first.Source.Title} on Disk {source.Number} is shrunk first to {DiskClonePlanner.FormatSize(first.Size)}." : string.Empty;
        if (WpfMessageBox.Show(this,
                $"Erase Disk {target.Number} ({target.Name}, {DiskClonePlanner.FormatSize(target.Size)}) and copy Disk {source.Number} ({source.Name}) onto it?{shrink}\n\nEverything now on Disk {target.Number} is lost. Keep this PC switched on until it finishes.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        CloneOptions options = new(UsedOnlyCheck.IsChecked == true, BootableCheck.IsChecked == true, VerifyCheck.IsChecked == true);
        bool ok = await RunAsync(token => _service.CloneAsync(source, target, plan, options, Progress(), token));
        if (ok)
        {
            ConfirmDiskBox.Clear();
            WpfMessageBox.Show(this,
                $"Disk {source.Number} was cloned to Disk {target.Number}.\n\nNext:\n1. Shut down the PC.\n2. Swap the disks, or choose the new disk ({target.Name}) first in the BIOS/UEFI boot menu.\n3. Start Windows from it and check that everything works before wiping the old disk.\n\nIf Windows Recovery shows as disabled on the new disk (reagentc /info), run \"reagentc /disable\" and then \"reagentc /enable\" as administrator.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        await LoadDisksAsync();
    }

    // ---- One partition ---------------------------------------------------------------------------------------

    private void PartitionTargetDisk_Changed(object sender, SelectionChangedEventArgs e)
    {
        PartitionSpaceComboBox.ItemsSource = PartitionTargetDiskComboBox.SelectedItem is CloneDisk disk
            ? disk.Style == "RAW"
                ? new[] { new CloneTargetSpace(DiskClonePlanner.MiB, DiskClonePlanner.AlignDown(disk.Size - 2 * DiskClonePlanner.MiB) - DiskClonePlanner.MiB, null) }
                : DiskClonePlanner.TargetSpaces(disk)
            : null;
        PartitionSpaceComboBox.SelectedIndex = PartitionSpaceComboBox.Items.Count > 0 ? 0 : -1;
        UpdatePartitionPlan();
    }

    private void PartitionSelection_Changed(object sender, RoutedEventArgs e) => UpdatePartitionPlan();

    private void UpdatePartitionPlan()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (PartitionSourceComboBox.SelectedItem is not PartitionChoice source || PartitionTargetDiskComboBox.SelectedItem is not CloneDisk target
            || PartitionSpaceComboBox.SelectedItem is not CloneTargetSpace space)
        {
            _partitionPlan = null;
            PartitionPlanText.Text = PartitionTargetDiskComboBox.SelectedItem is CloneDisk ? "There is no free space or partition to copy to on that disk." : string.Empty;
            UpdateStartButtons();
            return;
        }

        _partitionPlan = DiskClonePlanner.PlanPartitionCopy(source.Partition, source.Disk.LogicalSectorSize, target, space,
            FillSpaceCheck.IsChecked == true && source.Partition.IsNtfs);
        PlannedPartition planned = _partitionPlan.Partitions[0];
        PartitionPlanText.Text = _partitionPlan.Problems.Count > 0
            ? string.Join("\n", _partitionPlan.Problems)
            : $"{source.Partition.Title} ({DiskClonePlanner.FormatSize(source.Partition.Size)}) is copied to Disk {target.Number} as a {DiskClonePlanner.FormatSize(planned.Size)} partition"
              + (space.Replaces is { } replaced ? $", replacing {replaced.Title} (its data is lost)." : ".")
              + (planned.Mode == CopyMode.SnapshotUsed ? " It is read from a snapshot, used space only." : " Every sector is copied.");
        PartitionPlanText.SetResourceReference(TextBlock.ForegroundProperty, _partitionPlan.Problems.Count > 0 ? "DangerBrush" : space.Replaces is null ? "TextBrush" : "WarningBrush");
        UpdateStartButtons();
    }

    private async void StartPartitionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_partitionPlan is not { CanStart: true } plan || PartitionSourceComboBox.SelectedItem is not PartitionChoice source
            || PartitionTargetDiskComboBox.SelectedItem is not CloneDisk target || PartitionSpaceComboBox.SelectedItem is not CloneTargetSpace space)
        {
            return;
        }

        string warning = space.Replaces is { } replaced ? $"\n\n{replaced.Title} on Disk {target.Number} is deleted first; its data is lost." : string.Empty;
        if (WpfMessageBox.Show(this, $"Copy {source.Partition.Title} from Disk {source.Disk.Number} to Disk {target.Number}?{warning}",
                Title, MessageBoxButton.YesNo, space.Replaces is null ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        char? letter = (LetterComboBox.SelectedItem as LetterChoice)?.Letter;
        bool ok = await RunAsync(token => _service.CopyPartitionAsync(source.Disk, target, plan, space, letter, Progress(), token));
        if (ok)
        {
            WpfMessageBox.Show(this, $"{source.Partition.Title} was copied to Disk {target.Number}.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        await LoadDisksAsync();
    }

    // ---- Make a disk start Windows -------------------------------------------------------------------------

    private void BootDisk_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            FillBootWindowsChoices();
            UpdateBootPlan();
        }
    }

    private void FillBootWindowsChoices()
    {
        // NTFS partitions that could hold Windows, the largest first (the Windows partition usually is).
        List<PartitionChoice> choices = BootDiskComboBox.SelectedItem is CloneDisk disk
            ? disk.Partitions.Where(partition => partition.IsNtfs && !partition.IsRecovery).OrderByDescending(partition => partition.Size)
                .Select(partition => new PartitionChoice(disk, partition)).ToList()
            : new List<PartitionChoice>();
        BootWindowsComboBox.ItemsSource = choices;
        BootWindowsComboBox.SelectedIndex = choices.Count > 0 ? 0 : -1;
    }

    private void BootSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateBootPlan();

    private void UpdateBootPlan()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (BootDiskComboBox.SelectedItem is not CloneDisk disk)
        {
            _bootPlan = null;
            BootPlanText.Text = "No other disk with partitions is connected.";
            BootPlanText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            UpdateStartButtons();
            return;
        }

        if (BootWindowsComboBox.ItemsSource is null)
        {
            FillBootWindowsChoices();
        }

        _bootPlan = DiskClonePlanner.PlanBootSetup(disk, (BootWindowsComboBox.SelectedItem as PartitionChoice)?.Partition);
        BootPlanText.Text = _bootPlan.Problems.Count > 0
            ? string.Join("\n", _bootPlan.Problems)
            : $"Boot files go to {_bootPlan.Boot!.Title} on Disk {disk.Number} ({(disk.Style == "GPT" ? "UEFI" : "BIOS")} start)"
              + (_bootPlan.SetActive ? ", and the Windows partition is made active" : string.Empty)
              + $". The Windows now on {_bootPlan.Windows!.Title} becomes drive C when the PC starts from this disk.";
        BootPlanText.SetResourceReference(TextBlock.ForegroundProperty, _bootPlan.Problems.Count > 0 ? "DangerBrush" : "TextBrush");
        UpdateStartButtons();
    }

    private async void MakeBootableButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bootPlan is not { CanStart: true } plan || BootDiskComboBox.SelectedItem is not CloneDisk disk)
        {
            return;
        }

        if (WpfMessageBox.Show(this,
                $"Make Disk {disk.Number} ({disk.Name}) start the Windows on {plan.Windows!.Title}?\n\nThe boot files on its {plan.Boot!.Title} are replaced. The disk Windows runs from now is not changed.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        bool ok = await RunAsync(token => _service.MakeDiskStartWindowsAsync(disk, plan, Progress(), token));
        if (ok)
        {
            WpfMessageBox.Show(this,
                $"Disk {disk.Number} is set up to start Windows.\n\nShut down, then start from it: swap the disks, or choose it first in the BIOS/UEFI boot menu. Keep the old disk until Windows runs well from the new one.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        await LoadDisksAsync();
    }

    // ---- Running -------------------------------------------------------------------------------------------

    private IProgress<CloneProgress> Progress() => new Progress<CloneProgress>(update =>
    {
        if (update.LogLine is { } line)
        {
            LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
            LogBox.ScrollToEnd();
            return;
        }

        StageText.Text = update.Stage;
        CopyProgress.Value = update.Fraction;
        DetailText.Text = update.Total > 1
            ? $"{DiskClonePlanner.FormatSize((ulong)update.Done)} of {DiskClonePlanner.FormatSize((ulong)update.Total)}"
              + (update.BytesPerSecond > 0 ? $"  •  {update.BytesPerSecond / 1024 / 1024:0} MB/s" : string.Empty)
              + (update.Remaining is TimeSpan left ? $"  •  {(left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes} min" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min")} left" : string.Empty)
            : string.Empty;
    });

    /// <summary>Runs a clone or copy with the window locked, the PC kept awake and Cancel available. Returns true when it finished.</summary>
    private async Task<bool> RunAsync(Func<CancellationToken, Task> work)
    {
        _running = new CancellationTokenSource();
        ProgressPanel.Visibility = Visibility.Visible;
        ModeTabs.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        LogBox.Clear();
        UpdateStartButtons();
        SetThreadExecutionState(EsContinuous | EsSystemRequired);
        try
        {
            await work(_running.Token);
            StatusLine.Text = "Finished.";
            return true;
        }
        catch (OperationCanceledException)
        {
            StageText.Text = "Cancelled";
            StatusLine.Text = "Cancelled. The disk copied to is incomplete: clone again, or clean it in Disk tools. The disk copied from was not changed.";
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception
            or System.Management.ManagementException or System.Text.Json.JsonException)
        {
            ApplicationLogService.WriteException("Disk clone", exception);
            StageText.Text = "Stopped";
            LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  Stopped: {exception.Message}{Environment.NewLine}");
            StatusLine.Text = "Stopped. The disk copied from was not changed.";
            WpfMessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            SetThreadExecutionState(EsContinuous);
            _running.Dispose();
            _running = null;
            ModeTabs.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            UpdateStartButtons();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running is not null && WpfMessageBox.Show(this, "Cancel? The disk being copied to is left incomplete.", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            _running.Cancel();
            CancelButton.IsEnabled = false;
            StageText.Text = "Cancelling...";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_running is not null)
        {
            e.Cancel = true;
            WpfMessageBox.Show(this, "A copy is running. Cancel it first.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
