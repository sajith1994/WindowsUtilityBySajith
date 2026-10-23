using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using WindowsUtility.Models;
using WindowsUtility.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace WindowsUtility.Dialogs;

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
    private BackupPlan? _backupPlan;
    private BackupImage? _restoreImage;
    private ClonePlan? _restorePlan;
    private BackupImage? _openedBackup;
    private CancellationTokenSource? _running;
    private readonly BackupScheduleStore _scheduleStore = new();
    private List<BackupSchedule> _schedules = new();
    private BackupSchedule? _editingSchedule;
    private readonly System.Windows.Threading.DispatcherTimer _scheduleRefresh = new() { Interval = TimeSpan.FromSeconds(20) };

    public sealed record ScheduleRow(BackupSchedule Schedule, string Name, string What, string Where, string When, string LastRun, string NextRun);

    public sealed record DiskChoice(string Text, CloneDisk? Disk);

    public sealed class DayPick
    {
        public DayPick(DayOfWeek day, bool isChecked)
        {
            Day = day;
            IsChecked = isChecked;
        }

        public DayOfWeek Day { get; }
        public string Text => Day.ToString();
        public bool IsChecked { get; set; }
    }

    /// <summary>A partition ticked for a backup.</summary>
    public sealed class PartitionPick
    {
        public PartitionPick(ClonePartition partition)
        {
            Partition = partition;
            Text = $"{partition.Title}  •  {DiskClonePlanner.FormatSize(partition.Size)} {partition.FileSystem}"
                + (partition.UsedBytes > 0 ? $" ({DiskClonePlanner.FormatSize(partition.UsedBytes)} used)" : string.Empty)
                + (partition.IsBoot ? "  •  Windows" : partition.IsEfi ? "  •  needed to start Windows" : string.Empty);
        }

        public ClonePartition Partition { get; }
        public string Text { get; }
        public bool IsChecked { get; set; } = true;
    }

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
        ScheduleHourComboBox.ItemsSource = Enumerable.Range(0, 24).Select(hour => hour.ToString("00")).ToList();
        ScheduleMinuteComboBox.ItemsSource = new[] { "00", "15", "30", "45" };
        ScheduleMonthDayComboBox.ItemsSource = Enumerable.Range(1, 28).ToList();
        ScheduleKeepComboBox.ItemsSource = Enumerable.Range(1, 10).ToList();
        Loaded += async (_, _) => await LoadDisksAsync();
        Closing += Window_Closing;
        _scheduleRefresh.Tick += (_, _) => RefreshSchedules();
        _scheduleRefresh.Start();
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

            int? backupDisk = (BackupDiskComboBox.SelectedItem as CloneDisk)?.Number;
            BackupDiskComboBox.ItemsSource = _disks;
            BackupDiskComboBox.SelectedItem = _disks.FirstOrDefault(disk => disk.Number == backupDisk) ?? _disks.FirstOrDefault(disk => disk.HasWindows) ?? _disks.FirstOrDefault();
            FillBackupPartitions();
            int? restoreDisk = (RestoreTargetComboBox.SelectedItem as CloneDisk)?.Number;
            RestoreTargetComboBox.ItemsSource = TargetDiskComboBox.ItemsSource;
            RestoreTargetComboBox.SelectedItem = ((IEnumerable<CloneDisk>)RestoreTargetComboBox.ItemsSource).FirstOrDefault(disk => disk.Number == restoreDisk)
                ?? ((IEnumerable<CloneDisk>)RestoreTargetComboBox.ItemsSource).FirstOrDefault();

            List<LetterChoice> letters = new() { new("Next free letter", null), new("No letter", '-') };
            HashSet<char> used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
            letters.AddRange(Enumerable.Range('D', 'Z' - 'D' + 1).Select(code => (char)code).Where(letter => !used.Contains(letter)).Select(letter => new LetterChoice($"{letter}:", letter)));
            LetterComboBox.ItemsSource = letters;
            LetterComboBox.SelectedIndex = 0;
            StatusLine.Text = $"{_disks.Count} disk{(_disks.Count == 1 ? "" : "s")} found. Tip: do the first clone with a spare disk, and keep the old disk until the new one starts Windows.";
            UpdateClonePlan();
            UpdatePartitionPlan();
            UpdateBootPlan();
            UpdateBackupPlan();
            UpdateRestorePlan();
            RefreshSchedules();
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
            DecryptButton.Visibility = Visibility.Collapsed;
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
        DecryptButton.Visibility = source.Partitions.Any(partition => partition.NeedsDecrypting) ? Visibility.Visible : Visibility.Collapsed;
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
        DecryptButton.IsEnabled = idle;
        PartitionDecryptButton.IsEnabled = idle;
        BackupDecryptButton.IsEnabled = idle;
        StartBackupButton.IsEnabled = idle && _backupPlan?.CanStart == true;
        StartRestoreButton.IsEnabled = idle && _restorePlan is { CanStart: true, ShrinkFirst: null } && RestoreTargetComboBox.SelectedItem is CloneDisk restoreTarget
            && RestoreConfirmBox.Text.Trim() == restoreTarget.Number.ToString() && _openedBackup is null;
        OpenBackupButton.IsEnabled = idle && _restoreImage is not null && _openedBackup is null;
        CloseBackupButton.IsEnabled = idle && _openedBackup is not null;
        bool selected = SchedulesGrid.SelectedItem is ScheduleRow;
        EditScheduleButton.IsEnabled = selected;
        RemoveScheduleButton.IsEnabled = selected;
        ToggleScheduleButton.IsEnabled = selected;
        RunScheduleButton.IsEnabled = idle && selected;
        ToggleScheduleButton.Content = SchedulesGrid.SelectedItem is ScheduleRow { Schedule.Enabled: false } ? "Turn on" : "Turn off";
    }

    // ---- Automatic backups ---------------------------------------------------------------------------------------

    private void RefreshSchedules()
    {
        if (!IsLoaded)
        {
            return;
        }

        Guid? selected = (SchedulesGrid.SelectedItem as ScheduleRow)?.Schedule.Id;
        _schedules = _scheduleStore.LoadSchedules();
        List<BackupScheduleState> states = _scheduleStore.LoadStates();
        DateTime now = DateTime.Now;
        List<ScheduleRow> rows = _schedules.Select(schedule =>
        {
            BackupScheduleState? state = states.FirstOrDefault(each => each.Id == schedule.Id);
            string lastRun = state is null || state.LastRunUtc is null ? "Never"
                : state.Running ? $"Running since {state.LastRunUtc.Value.ToLocalTime():HH:mm}"
                : $"{state.LastRunUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}: {(state.LastSucceeded == true ? "OK" : "Failed")}. {state.LastMessage}";
            string what = schedule.Kind == BackupKind.Folder ? $"Folder {schedule.SourceFolder}"
                : schedule.WindowsDisk ? "System image of the Windows disk" : $"System image of {schedule.DiskName}";
            return new ScheduleRow(schedule, schedule.Name + (schedule.Enabled ? string.Empty : " (off)"), what, schedule.Destination, BackupScheduling.Describe(schedule), lastRun,
                schedule.Enabled ? BackupScheduling.NextRun(schedule, now).ToString("ddd yyyy-MM-dd HH:mm") : "Off");
        }).ToList();
        SchedulesGrid.ItemsSource = rows;
        SchedulesGrid.SelectedItem = rows.FirstOrDefault(row => row.Schedule.Id == selected);
        UpdateStartButtons();
    }

    private void SchedulesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStartButtons();

    private void AddImageScheduleButton_Click(object sender, RoutedEventArgs e) => OpenScheduleEditor(new BackupSchedule { Kind = BackupKind.SystemImage, Name = "Windows image" });

    private void AddFolderScheduleButton_Click(object sender, RoutedEventArgs e) => OpenScheduleEditor(new BackupSchedule { Kind = BackupKind.Folder, Name = "Documents", Frequency = BackupFrequency.Daily, TimeOfDayMinutes = 20 * 60 });

    private void EditScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SchedulesGrid.SelectedItem is ScheduleRow row)
        {
            OpenScheduleEditor(row.Schedule);
        }
    }

    private void OpenScheduleEditor(BackupSchedule schedule)
    {
        _editingSchedule = schedule;
        bool folder = schedule.Kind == BackupKind.Folder;
        ScheduleEditorTitle.Text = (_schedules.Any(each => each.Id == schedule.Id) ? "Edit " : "New ") + (folder ? "folder backup" : "system image backup");
        ScheduleNameBox.Text = schedule.Name;
        ScheduleImagePanel.Visibility = folder ? Visibility.Collapsed : Visibility.Visible;
        ScheduleFolderPanel.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
        ScheduleKeepPanel.Visibility = folder ? Visibility.Collapsed : Visibility.Visible;
        List<DiskChoice> disks = new() { new("The disk Windows runs from", null) };
        disks.AddRange(_disks.Where(disk => !disk.IsBoot && disk.Style is "GPT" or "MBR").Select(disk => new DiskChoice(disk.DisplayText, disk)));
        ScheduleDiskComboBox.ItemsSource = disks;
        ScheduleDiskComboBox.SelectedItem = schedule.WindowsDisk ? disks[0] : disks.FirstOrDefault(choice => choice.Disk?.Name == schedule.DiskName) ?? disks[0];
        ScheduleKeepComboBox.SelectedItem = Math.Clamp(schedule.Keep, 1, 10);
        ScheduleSourceBox.Text = schedule.SourceFolder;
        ScheduleDestinationBox.Text = schedule.Destination;
        ScheduleUserBox.Text = schedule.ShareUser;
        SchedulePasswordBox.Password = BackupScheduleStore.Unprotect(schedule.ShareSecret);
        ScheduleFrequencyComboBox.SelectedIndex = (int)schedule.Frequency;
        ScheduleHourComboBox.SelectedItem = (schedule.TimeOfDayMinutes / 60).ToString("00");
        ScheduleMinuteComboBox.SelectedItem = (schedule.TimeOfDayMinutes % 60 / 15 * 15).ToString("00");
        ScheduleMonthDayComboBox.SelectedItem = Math.Clamp(schedule.DayOfMonth, 1, 28);
        ScheduleWeekDaysList.ItemsSource = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(day => new DayPick(day, schedule.WeekDays.Contains(day))).ToList();
        ScheduleFrequency_Changed(this, null!);
        ScheduleEditor.Visibility = Visibility.Visible;
    }

    private void ScheduleFrequency_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ScheduleWeekDaysList.Visibility = ScheduleFrequencyComboBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleMonthDayPanel.Visibility = ScheduleFrequencyComboBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScheduleDestination_Changed(object sender, TextChangedEventArgs e) =>
        ScheduleSharePanel.Visibility = ScheduleDestinationBox.Text.TrimStart().StartsWith(@"\\", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    private void BrowseScheduleSourceButton_Click(object sender, RoutedEventArgs e) => BrowseFolder(ScheduleSourceBox, "Choose the folder to back up");

    private void BrowseScheduleDestinationButton_Click(object sender, RoutedEventArgs e) => BrowseFolder(ScheduleDestinationBox, "Choose where to save the backups (another partition, an external disk or a network share)");

    private static void BrowseFolder(System.Windows.Controls.TextBox box, string description)
    {
        using System.Windows.Forms.FolderBrowserDialog dialog = new() { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(box.Text.Trim()))
        {
            dialog.SelectedPath = box.Text.Trim();
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            box.Text = dialog.SelectedPath;
        }
    }

    private void SaveScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editingSchedule is not { } schedule)
        {
            return;
        }

        schedule.Name = ScheduleNameBox.Text.Trim();
        schedule.Destination = ScheduleDestinationBox.Text.Trim();
        schedule.SourceFolder = ScheduleSourceBox.Text.Trim();
        schedule.ShareUser = schedule.Destination.StartsWith(@"\\", StringComparison.Ordinal) ? ScheduleUserBox.Text.Trim() : string.Empty;
        schedule.ShareSecret = schedule.ShareUser.Length > 0 ? BackupScheduleStore.Protect(SchedulePasswordBox.Password) : string.Empty;
        DiskChoice? disk = ScheduleDiskComboBox.SelectedItem as DiskChoice;
        schedule.WindowsDisk = disk?.Disk is null;
        schedule.DiskName = disk?.Disk?.Name ?? string.Empty;
        schedule.DiskSize = disk?.Disk?.Size ?? 0;
        schedule.Keep = ScheduleKeepComboBox.SelectedItem as int? ?? 3;
        schedule.Frequency = (BackupFrequency)Math.Max(0, ScheduleFrequencyComboBox.SelectedIndex);
        schedule.TimeOfDayMinutes = int.Parse((string?)ScheduleHourComboBox.SelectedItem ?? "2") * 60 + int.Parse((string?)ScheduleMinuteComboBox.SelectedItem ?? "0");
        schedule.DayOfMonth = ScheduleMonthDayComboBox.SelectedItem as int? ?? 1;
        schedule.WeekDays = (ScheduleWeekDaysList.ItemsSource as IEnumerable<DayPick> ?? Array.Empty<DayPick>()).Where(pick => pick.IsChecked).Select(pick => pick.Day).ToList();

        string? problem = schedule.Name.Length == 0 ? "Give the backup a name."
            : schedule.Destination.Length == 0 ? "Choose where to save the backup."
            : schedule.Kind == BackupKind.Folder && !Directory.Exists(schedule.SourceFolder) ? "The folder to back up was not found."
            : schedule.Kind == BackupKind.Folder && schedule.Destination.StartsWith(schedule.SourceFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? "The destination cannot be inside the folder it backs up."
            : schedule.Frequency == BackupFrequency.Weekly && schedule.WeekDays.Count == 0 ? "Tick at least one day of the week."
            : null;
        if (problem is not null)
        {
            WpfMessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_schedules.Any(each => each.Id == schedule.Id))
        {
            _schedules.Add(schedule);
        }

        _scheduleStore.SaveSchedules(_schedules);
        ScheduleEditor.Visibility = Visibility.Collapsed;
        _editingSchedule = null;
        RefreshSchedules();
        StatusLine.Text = $"\"{schedule.Name}\" is saved; it next runs {BackupScheduling.NextRun(schedule, DateTime.Now):ddd yyyy-MM-dd HH:mm}. Run now tries it straight away.";
    }

    private void CancelScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        ScheduleEditor.Visibility = Visibility.Collapsed;
        _editingSchedule = null;
    }

    private void ToggleScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SchedulesGrid.SelectedItem is ScheduleRow row)
        {
            row.Schedule.Enabled = !row.Schedule.Enabled;
            _scheduleStore.SaveSchedules(_schedules);
            RefreshSchedules();
        }
    }

    private void RemoveScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SchedulesGrid.SelectedItem is not ScheduleRow row || WpfMessageBox.Show(this, $"Remove the automatic backup \"{row.Schedule.Name}\"? Backups already made stay where they are.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _schedules.RemoveAll(each => each.Id == row.Schedule.Id);
        _scheduleStore.SaveSchedules(_schedules);
        _scheduleStore.RemoveState(row.Schedule.Id);
        RefreshSchedules();
    }

    private async void RunScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SchedulesGrid.SelectedItem is not ScheduleRow row)
        {
            return;
        }

        BackupSchedule schedule = row.Schedule;
        DateTime started = DateTime.UtcNow;
        _scheduleStore.UpdateState(new BackupScheduleState { Id = schedule.Id, LastRunUtc = started, Running = true, LastMessage = "Running from the dashboard..." });
        RefreshSchedules();
        BackupRunResult? result = null;
        bool finished = await RunAsync(async token => result = await new BackupRunner().RunAsync(schedule, Progress(), token), "Cancelled.");
        _scheduleStore.UpdateState(new BackupScheduleState
        {
            Id = schedule.Id, LastRunUtc = started, LastSucceeded = finished && result?.Succeeded == true,
            LastMessage = result?.Message ?? (finished ? "Finished." : "Cancelled from the dashboard.")
        });
        RefreshSchedules();
        if (finished && result is not null)
        {
            WpfMessageBox.Show(this, result.Message, Title, MessageBoxButton.OK, result.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }

    private async void DecryptButton_Click(object sender, RoutedEventArgs e)
    {
        List<ClonePartition> encrypted = sender == DecryptButton
            ? (SourceDiskComboBox.SelectedItem as CloneDisk)?.Partitions.Where(partition => partition.NeedsDecrypting).ToList() ?? new()
            : sender == BackupDecryptButton
                ? ChosenBackupPartitions().Where(partition => partition.NeedsDecrypting).ToList()
                : PartitionSourceComboBox.SelectedItem is PartitionChoice { Partition.NeedsDecrypting: true } choice ? new() { choice.Partition } : new();
        if (encrypted.Count == 0)
        {
            return;
        }

        string names = string.Join(", ", encrypted.Select(partition => partition.Title));
        if (WpfMessageBox.Show(this,
                $"Turn BitLocker off on {names} and decrypt it?\n\nThe data stays as it is; only the encryption is removed, so it can be copied. You can turn BitLocker on again on the new disk. Decrypting takes from seconds to a few minutes per partition.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunAsync(token => _service.DecryptAsync(encrypted, Progress(), token),
            "Stopped waiting. Windows carries on decrypting; select Refresh disks in a while.");
        await LoadDisksAsync();
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
            PartitionDecryptButton.Visibility = Visibility.Collapsed;
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
              + (planned.Mode == CopyMode.SnapshotUsed ? " It is read from a snapshot, used space only." : " Every sector is copied.")
              + string.Concat(_partitionPlan.Notes.Select(note => "\n" + note));
        PartitionPlanText.SetResourceReference(TextBlock.ForegroundProperty, _partitionPlan.Problems.Count > 0 ? "DangerBrush" : space.Replaces is null ? "TextBrush" : "WarningBrush");
        PartitionDecryptButton.Visibility = source.Partition.NeedsDecrypting ? Visibility.Visible : Visibility.Collapsed;
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

    // ---- Back up to an image file ------------------------------------------------------------------------------

    private void BackupDisk_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            FillBackupPartitions();
            UpdateBackupPlan();
        }
    }

    private void FillBackupPartitions()
    {
        BackupPartitionsList.ItemsSource = BackupDiskComboBox.SelectedItem is CloneDisk disk
            ? disk.Partitions.Where(partition => !partition.IsMsr).OrderBy(partition => partition.Offset).Select(partition => new PartitionPick(partition)).ToList()
            : new List<PartitionPick>();
    }

    private List<ClonePartition> ChosenBackupPartitions() =>
        (BackupPartitionsList.ItemsSource as IEnumerable<PartitionPick> ?? Array.Empty<PartitionPick>()).Where(pick => pick.IsChecked).Select(pick => pick.Partition).ToList();

    private void BackupSelection_Changed(object sender, RoutedEventArgs e) => UpdateBackupPlan();

    private void BackupFolder_Changed(object sender, TextChangedEventArgs e)
    {
        SharePanel.Visibility = BackupFolderBox.Text.TrimStart().StartsWith(@"\\", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        UpdateBackupPlan();
    }

    private void BrowseBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using System.Windows.Forms.FolderBrowserDialog dialog = new()
        {
            Description = "Choose where to save the backup (another partition, an external disk or a network share)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (Directory.Exists(BackupFolderBox.Text.Trim()))
        {
            dialog.SelectedPath = BackupFolderBox.Text.Trim();
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            BackupFolderBox.Text = dialog.SelectedPath;
        }
    }

    private void UpdateBackupPlan()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (BackupDiskComboBox.SelectedItem is not CloneDisk disk)
        {
            _backupPlan = null;
            BackupPlanText.Text = "No disk to back up.";
            BackupDecryptButton.Visibility = Visibility.Collapsed;
            UpdateStartButtons();
            return;
        }

        List<ClonePartition> chosen = ChosenBackupPartitions();
        string folder = BackupFolderBox.Text.Trim();
        string? volume = folder.Length > 0 ? DiskCloneService.VolumePathOf(folder) : null;
        ClonePartition? destinationOn = volume is null ? null : _disks.SelectMany(each => each.Partitions).FirstOrDefault(partition => partition.VolumePath.Equals(volume, StringComparison.OrdinalIgnoreCase));
        ulong? free = folder.Length > 0 ? DiskCloneService.FreeSpace(folder) : null;
        BackupPlan plan = DiskClonePlanner.PlanBackup(disk, chosen, destinationOn, free);
        if (folder.Length == 0)
        {
            plan = plan with { Problems = plan.Problems.Prepend("Choose where to save the backup.").ToList() };
        }

        _backupPlan = plan;
        BackupPlanText.Text = plan.Problems.Count > 0
            ? string.Join("\n", plan.Problems)
            : string.Join("\n", plan.Notes.Append(
                $"About {DiskClonePlanner.FormatSize(plan.BytesToCopy)} is written to {folder}" + (free is ulong available ? $" ({DiskClonePlanner.FormatSize(available)} free there)." : ".")));
        BackupPlanText.SetResourceReference(TextBlock.ForegroundProperty, plan.Problems.Count > 0 ? "DangerBrush" : "TextBrush");
        BackupDecryptButton.Visibility = chosen.Any(partition => partition.NeedsDecrypting) ? Visibility.Visible : Visibility.Collapsed;
        UpdateStartButtons();
    }

    private async void StartBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_backupPlan is not { CanStart: true } plan || BackupDiskComboBox.SelectedItem is not CloneDisk disk)
        {
            return;
        }

        string folder = BackupFolderBox.Text.Trim();
        string user = BackupUserBox.Text.Trim();
        string password = BackupPasswordBox.Password;
        if (WpfMessageBox.Show(this, $"Back up Disk {disk.Number} ({disk.Name}) to {folder}?\n\nAbout {DiskClonePlanner.FormatSize(plan.BytesToCopy)} is written. Keep this PC switched on until it finishes.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        BackupImage? image = null;
        bool ok = await RunAsync(async token =>
        {
            if (folder.StartsWith(@"\\", StringComparison.Ordinal) && user.Length > 0)
            {
                await DiskCloneService.ConnectShareAsync(folder, user, password, token);
            }

            image = await _service.BackupAsync(disk, plan, folder, VerifyBackupCheck.IsChecked == true, Progress(), token);
        }, "Cancelled. The unfinished backup file was deleted; the disk backed up was not changed.");
        if (ok && image is not null)
        {
            RestoreFileBox.Text = image.ImagePath;
            WpfMessageBox.Show(this,
                $"The backup is finished:\n{image.ImagePath}\n\nKeep the .backup.json file next to it. To recover: install Windows Utility on the PC that has the new SSD connected (or on the new SSD after a fresh Windows), open Restore a backup, choose this file and the SSD. To get single files back, Open backup shows it as a disk in Explorer.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        UpdateBackupPlan();
    }

    // ---- Restore from an image file ------------------------------------------------------------------------------

    private void RestoreFile_Changed(object sender, TextChangedEventArgs e) => LoadRestoreImage();

    private void BrowseRestoreFileButton_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = "Choose a backup",
            Filter = "Backups (*.vhdx, *.backup.json)|*.vhdx;*.backup.json|All files|*.*",
            CheckFileExists = true
        };
        string current = RestoreFileBox.Text.Trim();
        if (current.Length > 0 && Directory.Exists(Path.GetDirectoryName(current)))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
        }

        if (dialog.ShowDialog(this) == true)
        {
            RestoreFileBox.Text = dialog.FileName;
        }
    }

    private void LoadRestoreImage()
    {
        if (!IsLoaded)
        {
            return;
        }

        string path = RestoreFileBox.Text.Trim();
        _restoreImage = null;
        if (path.Length == 0)
        {
            RestoreSummaryText.Text = "Choose a .vhdx backup made by this program; its .backup.json must be next to it.";
        }
        else if (!File.Exists(path))
        {
            RestoreSummaryText.Text = "That file was not found.";
        }
        else
        {
            try
            {
                _restoreImage = BackupImage.Load(path);
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                ApplicationLogService.WriteException("Disk backup: reading description", exception);
            }

            RestoreSummaryText.Text = _restoreImage is { } image
                ? $"Backup of {image.Title}: {image.DiskName}, {DiskClonePlanner.FormatSize(image.DiskSize)} {image.DiskStyle}, {DiskClonePlanner.FormatSize((ulong)image.BytesCopied)} of data"
                  + (image.Verified ? ", checked when made." : ".") + "\n"
                  + string.Join("\n", image.Partitions.Where(partition => !partition.Partition.IsMsr).Select(partition =>
                      $"  •  {partition.Partition.Title}: {DiskClonePlanner.FormatSize(partition.Partition.Size)} {partition.Partition.FileSystem}"
                      + (partition.BytesCopied > 0 ? $", {DiskClonePlanner.FormatSize((ulong)partition.BytesCopied)} backed up" : string.Empty)))
                  + (File.Exists(image.ImagePath) ? string.Empty : $"\nThe image file {image.ImageFileName} is missing next to the description.")
                : "This is not a backup made by this program (no matching .backup.json next to it), so it cannot be restored here. Windows can still attach a .vhdx in Disk Management.";
            if (_restoreImage is { } loaded && !File.Exists(loaded.ImagePath))
            {
                _restoreImage = null;
            }
        }

        UpdateRestorePlan();
    }

    private void RestoreSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateRestorePlan();

    private void UpdateRestorePlan()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (_restoreImage is not { } image || RestoreTargetComboBox.SelectedItem is not CloneDisk target)
        {
            _restorePlan = null;
            RestorePlanText.Text = _restoreImage is null ? string.Empty
                : RestoreTargetComboBox.Items.Count == 0 ? "No other disk is connected. Connect the new SSD (in a second slot, or with a USB adapter or enclosure), then select Refresh disks."
                : "Choose the disk to restore to.";
            RestorePlanText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            UpdateStartButtons();
            return;
        }

        ClonePlan plan = DiskClonePlanner.PlanClone(image.ToDisk(), target);
        if (plan.ShrinkFirst is { } shrink)
        {
            plan = plan with { Problems = plan.Problems.Append($"Disk {target.Number} ({DiskClonePlanner.FormatSize(target.Size)}) is smaller than the backed-up disk, and a backup cannot be shrunk; {shrink.Source.Title} needs its full {DiskClonePlanner.FormatSize(shrink.Source.Size)}.").ToList() };
        }

        _restorePlan = plan;
        RestorePlanText.Text = plan.Problems.Count > 0
            ? string.Join("\n", plan.Problems)
            : string.Join("\n", plan.Partitions.Where(planned => planned.Mode != CopyMode.Skip).Select(planned =>
                  $"{planned.Source.Title}: {DiskClonePlanner.FormatSize(planned.Source.Size)}" + (planned.Grows ? $" → {DiskClonePlanner.FormatSize(planned.Size)} (grows)" : string.Empty))
                  .Concat(plan.Notes).Append($"Everything on Disk {target.Number} ({target.Name}, {DiskClonePlanner.FormatSize(target.Size)}) is erased first."));
        RestorePlanText.SetResourceReference(TextBlock.ForegroundProperty, plan.Problems.Count > 0 ? "DangerBrush" : "TextBrush");
        RestoreBootableCheck.IsEnabled = image.HasWindows;
        RestoreBootableCheck.IsChecked = image.HasWindows && RestoreBootableCheck.IsChecked == true;
        RestoreConfirmPromptText.Text = $"To start, type {target.Number} (the number of the disk that will be erased):";
        UpdateStartButtons();
    }

    private async void StartRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_restorePlan is not { CanStart: true, ShrinkFirst: null } plan || _restoreImage is not { } image || RestoreTargetComboBox.SelectedItem is not CloneDisk target)
        {
            return;
        }

        if (WpfMessageBox.Show(this,
                $"Erase Disk {target.Number} ({target.Name}, {DiskClonePlanner.FormatSize(target.Size)}) and restore the backup of {image.Title} onto it?\n\nEverything now on Disk {target.Number} is lost. Keep this PC switched on until it finishes.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        bool ok = await RunAsync(token => _service.RestoreAsync(image, target, plan, RestoreBootableCheck.IsChecked == true, RestoreVerifyCheck.IsChecked == true, Progress(), token),
            "Cancelled. The disk restored to is incomplete: restore again, or clean it in Disk tools. The backup file was not changed.");
        if (ok)
        {
            RestoreConfirmBox.Clear();
            WpfMessageBox.Show(this,
                image.HasWindows
                    ? $"The backup was restored to Disk {target.Number}.\n\nNext:\n1. Shut down the PC.\n2. Put the disk in, or choose it first in the BIOS/UEFI boot menu.\n3. Start Windows from it.\n\nIf Windows Recovery shows as disabled (reagentc /info), run \"reagentc /disable\" and then \"reagentc /enable\" as administrator."
                    : $"The backup was restored to Disk {target.Number}.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        await LoadDisksAsync();
    }

    private async void OpenBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_restoreImage is not { } image)
        {
            return;
        }

        IReadOnlyList<string> letters = Array.Empty<string>();
        bool ok = await RunAsync(async token => letters = await _service.OpenBackupAsync(image, token), "Cancelled.");
        if (!ok)
        {
            return;
        }

        _openedBackup = image;
        if (letters.Count > 0)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", letters[^1] + "\\") { UseShellExecute = true });
            }
            catch (Win32Exception exception)
            {
                ApplicationLogService.WriteException("Disk backup: opening Explorer", exception);
            }

            StatusLine.Text = $"The backup is open as {string.Join(", ", letters)}. Copy what you need, then select Close backup.";
        }
        else
        {
            StatusLine.Text = "The backup is attached as a disk but got no drive letter; see Disk Management. Select Close backup when done.";
        }

        await LoadDisksAsync();
    }

    private async void CloseBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openedBackup is not { } image)
        {
            return;
        }

        bool ok = await RunAsync(token => _service.CloseBackupAsync(image, token), "Cancelled.");
        if (ok)
        {
            _openedBackup = null;
            StatusLine.Text = "The backup is closed.";
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
    private async Task<bool> RunAsync(Func<CancellationToken, Task> work, string? cancelledText = null)
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
            StatusLine.Text = cancelledText ?? "Cancelled. The disk copied to is incomplete: clone again, or clean it in Disk tools. The disk copied from was not changed.";
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
            return;
        }

        if (_openedBackup is not null)
        {
            WpfMessageBox.Show(this, "The backup is still open as a disk. It stays attached until it is closed in Disk Management or the PC restarts; nothing in it is harmed.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
