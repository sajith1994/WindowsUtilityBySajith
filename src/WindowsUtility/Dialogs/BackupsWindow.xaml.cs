using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WindowsUtility.Models;
using WindowsUtility.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace WindowsUtility.Dialogs;

/// <summary>
/// Backups without an administrator password: system images are made by the background service (which has the rights;
/// it accepts them from administrator accounts), folder backups run as the signed-in user. Restoring and cloning,
/// which write whole disks, open the administrator window.
/// </summary>
public partial class BackupsWindow : Window
{
    private readonly BackupScheduleStore _folderStore = BackupScheduleStore.ForUser();
    private readonly PrivilegedCommandClient _service = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(3) };
    private List<BackupSchedule> _imageSchedules = new();
    private List<BackupSchedule> _folderSchedules = new();
    private BackupSchedule? _editing;
    private bool _busy;

    public sealed record ScheduleRow(BackupSchedule? Schedule, Guid Id, string Name, string What, string Where, string When, string LastRun, string NextRun);

    public sealed record DiskChoice(string Text, CloneDisk? Disk);

    public sealed record FrequencyChoice(string Text, BackupFrequency Frequency, int UnitMinutes)
    {
        public override string ToString() => Text;
    }

    public sealed record IntervalChoice(string Text, int Minutes);

    public sealed record HistoryChoice(string Text, int Days);

    private static readonly HistoryChoice[] HistoryChoices =
    {
        new("7 days", 7), new("14 days", 14), new("30 days", 30), new("90 days", 90), new("a year", 365), new("for ever", 0)
    };

    private BackupSchedule? _restoring;

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

    /// <param name="tab">0 system image, 1 folders, 2 restore and clone.</param>
    public BackupsWindow(int tab = 0)
    {
        InitializeComponent();
        Tabs.SelectedIndex = Math.Clamp(tab, 0, Tabs.Items.Count - 1);
        HourComboBox.ItemsSource = Enumerable.Range(0, 24).Select(hour => hour.ToString("00")).ToList();
        MinuteComboBox.ItemsSource = new[] { "00", "15", "30", "45" };
        MonthDayComboBox.ItemsSource = Enumerable.Range(1, 28).ToList();
        KeepComboBox.ItemsSource = Enumerable.Range(1, 10).ToList();
        HistoryComboBox.ItemsSource = HistoryChoices;
        FullEveryComboBox.ItemsSource = new[] { 3, 5, 7, 10, 14, 30 };
        Loaded += (_, _) =>
        {
            // Folder backups 2.0.88 kept in the shared file move into this user's own list.
            BackupScheduleStore.MigrateLegacyFolderSchedules(_folderStore);
            Refresh();
        };
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();
        Closed += (_, _) => _refresh.Stop();
    }

    // ---- Lists -----------------------------------------------------------------------------------------------

    private void Refresh()
    {
        if (!IsLoaded)
        {
            return;
        }

        _imageSchedules = BackupScheduleStore.LoadPublicImageSchedules();
        List<BackupScheduleState> imageStates = BackupScheduleStore.LoadPublicImageStates();
        List<ScheduleRow> images = _imageSchedules.Select(schedule => Row(schedule, imageStates.FirstOrDefault(state => state.Id == schedule.Id))).ToList();
        // "Back up now" runs are not schedules; the last one is shown so its progress and result can be seen.
        images.AddRange(imageStates.Where(state => state.Name.Length > 0 && _imageSchedules.All(schedule => schedule.Id != state.Id))
            .OrderByDescending(state => state.LastRunUtc).Take(1)
            .Select(state => new ScheduleRow(null, state.Id, state.Name, "Windows disk", string.Empty, "Once", LastRunText(state), string.Empty)));
        SetRows(ImageGrid, images);

        _folderSchedules = _folderStore.LoadSchedules();
        List<BackupScheduleState> folderStates = _folderStore.LoadStates();
        SetRows(FolderGrid, _folderSchedules.Select(schedule => Row(schedule, folderStates.FirstOrDefault(state => state.Id == schedule.Id))).ToList());
        UpdateButtons();
    }

    private static void SetRows(System.Windows.Controls.DataGrid grid, List<ScheduleRow> rows)
    {
        Guid? selected = (grid.SelectedItem as ScheduleRow)?.Id;
        grid.ItemsSource = rows;
        grid.SelectedItem = rows.FirstOrDefault(row => row.Id == selected);
    }

    private static ScheduleRow Row(BackupSchedule schedule, BackupScheduleState? state)
    {
        string what = schedule.Kind == BackupKind.Folder ? schedule.SourceFolder
            : schedule.WindowsDisk ? "Windows disk" : schedule.DiskName;
        string where = schedule.Kind == BackupKind.Folder ? FolderBackupRunner.TargetFolder(schedule) : schedule.Destination;
        string next = !schedule.Enabled ? "Off"
            : schedule.Frequency == BackupFrequency.Live ? "On change"
            : BackupScheduling.NextRun(schedule, DateTime.Now).ToString("ddd dd MMM HH:mm");
        return new ScheduleRow(schedule, schedule.Id, schedule.Name + (schedule.Enabled ? string.Empty : " (off)"), what, where,
            BackupScheduling.Describe(schedule), LastRunText(state), next);
    }

    private static string LastRunText(BackupScheduleState? state)
    {
        if (state?.LastRunUtc is not DateTime ran)
        {
            return "Never";
        }

        if (state.Running)
        {
            string stage = state.Stage.Length > 0 ? state.Stage : "Running";
            return state.Fraction > 0 ? $"{stage} {state.Fraction:P0}" : $"{stage} (since {ran.ToLocalTime():HH:mm})";
        }

        return $"{ran.ToLocalTime():dd MMM HH:mm}: {(state.LastSucceeded == true ? "OK" : "Failed")}. {state.LastMessage}";
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        ScheduleRow? image = ImageGrid.SelectedItem as ScheduleRow;
        bool isSchedule = image?.Schedule is not null;
        EditImageButton.IsEnabled = isSchedule;
        ToggleImageButton.IsEnabled = isSchedule;
        RunImageButton.IsEnabled = isSchedule;
        RemoveImageButton.IsEnabled = isSchedule;
        ToggleImageButton.Content = image?.Schedule is { Enabled: false } ? "Turn on" : "Turn off";

        ScheduleRow? folder = FolderGrid.SelectedItem as ScheduleRow;
        EditFolderButton.IsEnabled = folder is not null;
        ToggleFolderButton.IsEnabled = folder is not null;
        RunFolderButton.IsEnabled = folder is not null && !_busy;
        RestoreFolderButton.IsEnabled = folder is not null && !_busy;
        OpenFolderCopyButton.IsEnabled = folder is not null;
        RemoveFolderButton.IsEnabled = folder is not null;
        ToggleFolderButton.Content = folder?.Schedule is { Enabled: false } ? "Turn on" : "Turn off";
    }

    // ---- System image (through the background service) ------------------------------------------------------

    private void NowDestination_Changed(object sender, TextChangedEventArgs e) =>
        NowSharePanel.Visibility = NowDestinationBox.Text.TrimStart().StartsWith(@"\\", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    private void BrowseNowDestinationButton_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(NowDestinationBox, "Choose where to save the backup (another partition, an external disk or a network share)");

    private async void BackUpNowButton_Click(object sender, RoutedEventArgs e)
    {
        string destination = NowDestinationBox.Text.Trim();
        if (destination.Length == 0)
        {
            WpfMessageBox.Show(this, "Choose where to save the backup.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool share = destination.StartsWith(@"\\", StringComparison.Ordinal);
        await SendAsync(PrivilegedServiceActions.BackUpImageNow, new Dictionary<string, string>
        {
            ["destination"] = destination,
            ["user"] = share ? NowUserBox.Text.Trim() : string.Empty,
            ["password"] = share ? NowPasswordBox.Password : string.Empty
        });
    }

    private void AddImageButton_Click(object sender, RoutedEventArgs e) =>
        OpenEditor(new BackupSchedule { Kind = BackupKind.SystemImage, Name = "Windows image", Frequency = BackupFrequency.Weekly, TimeOfDayMinutes = 12 * 60 });

    private void EditImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImageGrid.SelectedItem is ScheduleRow { Schedule: { } schedule })
        {
            OpenEditor(Copy(schedule));
        }
    }

    private async void ToggleImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImageGrid.SelectedItem is ScheduleRow { Schedule: { } schedule })
        {
            BackupSchedule changed = Copy(schedule);
            changed.Enabled = !changed.Enabled;
            await SaveImageAsync(changed, string.Empty);
        }
    }

    private async void RunImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImageGrid.SelectedItem is ScheduleRow { Schedule: { } schedule })
        {
            await SendAsync(PrivilegedServiceActions.RunImageBackup, new Dictionary<string, string> { ["id"] = schedule.Id.ToString() });
        }
    }

    private async void RemoveImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (ImageGrid.SelectedItem is ScheduleRow { Schedule: { } schedule }
            && WpfMessageBox.Show(this, $"Remove the automatic backup \"{schedule.Name}\"? Backups already made stay where they are.", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            await SendAsync(PrivilegedServiceActions.RemoveImageBackup, new Dictionary<string, string> { ["id"] = schedule.Id.ToString() });
        }
    }

    /// <summary>The service keeps the password (encrypted); the schedule itself goes without one.</summary>
    private async Task<bool> SaveImageAsync(BackupSchedule schedule, string password)
    {
        BackupSchedule sent = Copy(schedule);
        sent.ShareSecret = string.Empty;
        return await SendAsync(PrivilegedServiceActions.SaveImageBackup, new Dictionary<string, string>
        {
            ["schedule"] = JsonSerializer.Serialize(sent),
            ["password"] = password
        });
    }

    /// <summary>Asks the background service; shows its answer. Returns whether it agreed.</summary>
    private async Task<bool> SendAsync(string action, Dictionary<string, string> arguments)
    {
        try
        {
            PrivilegedServiceCommandResponse response = await _service.SendAsync(action, arguments, TimeSpan.FromSeconds(30));
            StatusLine.Text = response.Message;
            Refresh();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            StatusLine.Text = exception.Message;
            WpfMessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    // ---- Folders (as this user) ------------------------------------------------------------------------------

    private void AddFolderButton_Click(object sender, RoutedEventArgs e) =>
        OpenEditor(new BackupSchedule
        {
            Kind = BackupKind.Folder, Name = "Documents", Frequency = BackupFrequency.Interval, IntervalMinutes = 60,
            SourceFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        });

    private void EditFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (FolderGrid.SelectedItem is ScheduleRow { Schedule: { } schedule })
        {
            OpenEditor(Copy(schedule));
        }
    }

    private void ToggleFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (FolderGrid.SelectedItem is ScheduleRow { Schedule: { } schedule })
        {
            _folderSchedules.First(each => each.Id == schedule.Id).Enabled = !schedule.Enabled;
            _folderStore.SaveSchedules(_folderSchedules);
            Refresh();
        }
    }

    private async void RunFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (FolderGrid.SelectedItem is not ScheduleRow { Schedule: { } schedule })
        {
            return;
        }

        _busy = true;
        UpdateButtons();
        StatusLine.Text = $"Copying \"{schedule.Name}\"...";
        try
        {
            BackupRunResult result = await Task.Run(() => FolderBackupRunner.RunAsync(schedule, _folderStore,
                line => Dispatcher.BeginInvoke(() => StatusLine.Text = line), CancellationToken.None));
            StatusLine.Text = result.Message;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    // ---- Restore a folder as of a run ------------------------------------------------------------------------------

    private void RestoreFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (FolderGrid.SelectedItem is not ScheduleRow { Schedule: { } schedule })
        {
            return;
        }

        Editor.Visibility = Visibility.Collapsed;
        _restoring = schedule;
        RestoreTitle.Text = $"Restore \"{schedule.Name}\" from a date";
        List<BackupVersion> versions;
        try
        {
            versions = FolderBackupHistory.Versions(FolderBackupRunner.TargetFolder(schedule));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            versions = new List<BackupVersion>();
            StatusLine.Text = exception.Message;
        }

        VersionComboBox.ItemsSource = versions;
        VersionComboBox.SelectedIndex = versions.Count > 0 ? 0 : -1;
        RestoreWhereComboBox.SelectedIndex = 0;
        RestoreOtherBox.Text = string.Empty;
        VersionSummary.Text = versions.Count == 0 ? "This backup has not run since the history was added; its next run starts the history." : string.Empty;
        RestorePanel.Visibility = Visibility.Visible;
        Version_Changed(this, null!);
    }

    private void Version_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_restoring is null)
        {
            return;
        }

        if (VersionComboBox.SelectedItem is not BackupVersion version)
        {
            DoRestoreButton.IsEnabled = false;
            return;
        }

        try
        {
            List<BackupFileVersion> files = FolderBackupHistory.Files(version);
            VersionSummary.Text = $"{files.Count} files, {FolderBackupRunner.FormatBytes(files.Sum(file => file.Size))}, as the folder was on {version.Title}.";
            DoRestoreButton.IsEnabled = !_busy;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            VersionSummary.Text = $"This run's list could not be read: {exception.Message}";
            DoRestoreButton.IsEnabled = false;
        }
    }

    private void RestoreWhere_Changed(object sender, SelectionChangedEventArgs e) =>
        RestoreOtherPanel.Visibility = RestoreWhereComboBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

    private void BrowseRestoreOtherButton_Click(object sender, RoutedEventArgs e) => BrowseFolder(RestoreOtherBox, "Choose the folder to restore into");

    private async void DoRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_restoring is not { } schedule || VersionComboBox.SelectedItem is not BackupVersion version)
        {
            return;
        }

        bool original = RestoreWhereComboBox.SelectedIndex == 0;
        string destination = original ? schedule.SourceFolder.TrimEnd('\\') : RestoreOtherBox.Text.Trim();
        if (destination.Length == 0)
        {
            WpfMessageBox.Show(this, "Choose the folder to restore into.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string target = FolderBackupRunner.TargetFolder(schedule);
        if (destination.Equals(target, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase))
        {
            WpfMessageBox.Show(this, "Restore into the original folder or another folder, not into the backup itself.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (WpfMessageBox.Show(this,
                original
                    ? $"Put {schedule.SourceFolder} back as it was on {version.Title}?\n\nFiles changed since then are replaced (the backup's next run keeps today's versions in its history). Files added since then stay."
                    : $"Copy the folder as it was on {version.Title} into {destination}?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _busy = true;
        DoRestoreButton.IsEnabled = false;
        UpdateButtons();
        try
        {
            (int restored, int unchanged, List<string> missing) = await Task.Run(() => FolderBackupHistory.Restore(target, version, destination,
                (file, done, total) => Dispatcher.BeginInvoke(() => StatusLine.Text = $"Restoring {done} of {total}: {file}"), CancellationToken.None));
            string text = $"Restored {restored} file{(restored == 1 ? "" : "s")} as of {version.Title} into {destination}; {unchanged} were already the same."
                + (missing.Count > 0 ? $" {missing.Count} could not be found any more (history older than the days kept): {string.Join(", ", missing.Take(3))}{(missing.Count > 3 ? "..." : "")}" : string.Empty);
            StatusLine.Text = text;
            WpfMessageBox.Show(this, text, Title, MessageBoxButton.OK, missing.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            RestorePanel.Visibility = Visibility.Collapsed;
            _restoring = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusLine.Text = exception.Message;
            WpfMessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private void CancelRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        RestorePanel.Visibility = Visibility.Collapsed;
        _restoring = null;
    }

    private void OpenFolderCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (FolderGrid.SelectedItem is not ScheduleRow { Schedule: { } schedule })
        {
            return;
        }

        string target = FolderBackupRunner.TargetFolder(schedule);
        if (!Directory.Exists(target))
        {
            StatusLine.Text = $"{target} does not exist yet; it is made on the first run.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch (Win32Exception exception)
        {
            StatusLine.Text = exception.Message;
        }
    }

    private void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (FolderGrid.SelectedItem is not ScheduleRow { Schedule: { } schedule }
            || WpfMessageBox.Show(this, $"Remove the folder backup \"{schedule.Name}\"? The copies already made stay where they are.", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _folderSchedules.RemoveAll(each => each.Id == schedule.Id);
        _folderStore.SaveSchedules(_folderSchedules);
        _folderStore.RemoveState(schedule.Id);
        Refresh();
    }

    // ---- Editor ------------------------------------------------------------------------------------------------

    private async void OpenEditor(BackupSchedule schedule)
    {
        RestorePanel.Visibility = Visibility.Collapsed;
        _restoring = null;
        _editing = schedule;
        bool folder = schedule.Kind == BackupKind.Folder;
        HistoryPanel.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
        HistoryComboBox.SelectedItem = HistoryChoices.FirstOrDefault(choice => choice.Days == schedule.HistoryDays) ?? HistoryChoices[2];
        IncrementalPanel.Visibility = folder ? Visibility.Collapsed : Visibility.Visible;
        IncrementalCheck.IsChecked = schedule.Incremental;
        FullEveryComboBox.SelectedItem = ((int[])FullEveryComboBox.ItemsSource).Contains(schedule.FullEvery) ? schedule.FullEvery : 7;
        Incremental_Changed(this, null!);
        bool existing = (folder ? _folderSchedules : _imageSchedules).Any(each => each.Id == schedule.Id);
        EditorTitle.Text = (existing ? "Edit " : "New ") + (folder ? "folder backup" : "automatic system image backup");
        NameBox.Text = schedule.Name;
        SourceBox.Text = schedule.SourceFolder;
        DestinationBox.Text = schedule.Destination;
        UserBox.Text = schedule.ShareUser;
        PasswordBox.Password = folder ? _folderStore.Unprotect(schedule.ShareSecret) : string.Empty;
        PasswordLabel.Text = folder || !existing ? "Password (kept encrypted)" : "Password (empty keeps the saved one)";
        DiskPanel.Visibility = folder ? Visibility.Collapsed : Visibility.Visible;
        SourcePanel.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
        KeepPanel.Visibility = folder ? Visibility.Collapsed : Visibility.Visible;
        KeepComboBox.SelectedItem = Math.Clamp(schedule.Keep, 1, 10);
        HourComboBox.SelectedItem = (schedule.TimeOfDayMinutes / 60).ToString("00");
        MinuteComboBox.SelectedItem = (schedule.TimeOfDayMinutes % 60 / 15 * 15).ToString("00");
        MonthDayComboBox.SelectedItem = Math.Clamp(schedule.DayOfMonth, 1, 28);
        WeekDaysList.ItemsSource = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(day => new DayPick(day, schedule.WeekDays.Contains(day))).ToList();

        // A system image copies the whole disk, so it is never live and runs at most hourly.
        List<FrequencyChoice> frequencies = new();
        if (folder)
        {
            frequencies.Add(new("Every few minutes", BackupFrequency.Interval, 1));
        }

        frequencies.Add(new("Every few hours", BackupFrequency.Interval, 60));
        frequencies.Add(new("Every day", BackupFrequency.Daily, 0));
        frequencies.Add(new("Every week", BackupFrequency.Weekly, 0));
        frequencies.Add(new("Every month on a date", BackupFrequency.Monthly, 0));
        if (folder)
        {
            frequencies.Add(new("Live (as files change)", BackupFrequency.Live, 0));
        }

        FrequencyComboBox.ItemsSource = frequencies;
        bool inHours = schedule.IntervalMinutes % 60 == 0;
        FrequencyComboBox.SelectedItem = frequencies.FirstOrDefault(choice => choice.Frequency == schedule.Frequency
            && (choice.Frequency != BackupFrequency.Interval || (choice.UnitMinutes == 60) == inHours)) ?? frequencies[0];
        SelectInterval(schedule.IntervalMinutes);
        EditorNote.Text = folder
            ? "Runs as you while you are signed in. The copy, in a folder of the same name at the destination, follows the folder; files it replaces or removes are kept with their date in its _History folder, for Restore from a date."
            : "Made by the background service with administrator rights, also when nobody is signed in. Incremental runs are small and quick (the disk is compared with the last image and only the changed parts are written) but need the earlier images of their chain; each full image starts a new chain, and only the newest chains are kept.";
        Editor.Visibility = Visibility.Visible;

        if (!folder)
        {
            // Other disks are listed when they can be read; the Windows disk is always the first choice.
            List<DiskChoice> disks = new() { new("The disk Windows runs from", null) };
            DiskComboBox.ItemsSource = disks;
            DiskComboBox.SelectedIndex = 0;
            try
            {
                disks.AddRange((await new DiskCloneService().GetDisksAsync()).Where(disk => !disk.IsBoot && disk.Style is "GPT" or "MBR")
                    .Select(disk => new DiskChoice(disk.DisplayText, disk)));
                DiskComboBox.ItemsSource = disks.ToList();
                DiskComboBox.SelectedItem = schedule.WindowsDisk ? disks[0] : disks.FirstOrDefault(choice => choice.Disk?.Name == schedule.DiskName) ?? disks[0];
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or IOException or Win32Exception or KeyNotFoundException)
            {
                ApplicationLogService.WriteException("Backups: reading disks", exception);
            }
        }
    }

    private void Frequency_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FrequencyComboBox.SelectedItem is not FrequencyChoice choice)
        {
            return;
        }

        IntervalPanel.Visibility = choice.Frequency == BackupFrequency.Interval ? Visibility.Visible : Visibility.Collapsed;
        TimePanel.Visibility = choice.Frequency is BackupFrequency.Daily or BackupFrequency.Weekly or BackupFrequency.Monthly ? Visibility.Visible : Visibility.Collapsed;
        WeekDaysList.Visibility = choice.Frequency == BackupFrequency.Weekly ? Visibility.Visible : Visibility.Collapsed;
        MonthDayPanel.Visibility = choice.Frequency == BackupFrequency.Monthly ? Visibility.Visible : Visibility.Collapsed;
        if (choice.Frequency == BackupFrequency.Interval)
        {
            int current = (IntervalComboBox.SelectedItem as IntervalChoice)?.Minutes ?? _editing?.IntervalMinutes ?? 60;
            IntervalComboBox.ItemsSource = choice.UnitMinutes == 1
                ? new[] { 5, 10, 15, 20, 30, 45 }.Select(minutes => new IntervalChoice($"{minutes} minutes", minutes)).ToList()
                : new[] { 1, 2, 3, 4, 6, 8, 12 }.Select(hours => new IntervalChoice(hours == 1 ? "1 hour" : $"{hours} hours", hours * 60)).ToList();
            SelectInterval(current);
        }
    }

    private void SelectInterval(int minutes)
    {
        if (IntervalComboBox.ItemsSource is IEnumerable<IntervalChoice> choices)
        {
            List<IntervalChoice> list = choices.ToList();
            IntervalComboBox.SelectedItem = list.FirstOrDefault(choice => choice.Minutes == minutes) ?? list.MinBy(choice => Math.Abs(choice.Minutes - minutes));
        }
    }

    private void Incremental_Changed(object sender, RoutedEventArgs e) =>
        FullEveryPanel.Visibility = IncrementalCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void Destination_Changed(object sender, TextChangedEventArgs e) =>
        SharePanel.Visibility = DestinationBox.Text.TrimStart().StartsWith(@"\\", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    private void BrowseSourceButton_Click(object sender, RoutedEventArgs e) => BrowseFolder(SourceBox, "Choose the folder to back up");

    private void BrowseDestinationButton_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(DestinationBox, "Choose where to save the backups (another partition, an external disk or a network share)");

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } schedule || FrequencyComboBox.SelectedItem is not FrequencyChoice frequency)
        {
            return;
        }

        schedule.Name = NameBox.Text.Trim();
        schedule.SourceFolder = SourceBox.Text.Trim();
        schedule.Destination = DestinationBox.Text.Trim();
        schedule.ShareUser = schedule.Destination.StartsWith(@"\\", StringComparison.Ordinal) ? UserBox.Text.Trim() : string.Empty;
        schedule.Frequency = frequency.Frequency;
        schedule.IntervalMinutes = (IntervalComboBox.SelectedItem as IntervalChoice)?.Minutes ?? 60;
        schedule.TimeOfDayMinutes = int.Parse((string?)HourComboBox.SelectedItem ?? "2") * 60 + int.Parse((string?)MinuteComboBox.SelectedItem ?? "0");
        schedule.DayOfMonth = MonthDayComboBox.SelectedItem as int? ?? 1;
        schedule.WeekDays = (WeekDaysList.ItemsSource as IEnumerable<DayPick> ?? Array.Empty<DayPick>()).Where(pick => pick.IsChecked).Select(pick => pick.Day).ToList();
        schedule.Keep = KeepComboBox.SelectedItem as int? ?? 3;
        schedule.HistoryDays = (HistoryComboBox.SelectedItem as HistoryChoice)?.Days ?? 30;
        schedule.Incremental = IncrementalCheck.IsChecked == true;
        schedule.FullEvery = FullEveryComboBox.SelectedItem as int? ?? 7;
        DiskChoice? disk = DiskComboBox.SelectedItem as DiskChoice;
        schedule.WindowsDisk = disk?.Disk is null;
        schedule.DiskName = disk?.Disk?.Name ?? string.Empty;
        schedule.DiskSize = disk?.Disk?.Size ?? 0;

        if (schedule.Kind == BackupKind.Folder)
        {
            if (FolderBackupRunner.Validate(schedule) is { } problem)
            {
                WpfMessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            schedule.ShareSecret = schedule.ShareUser.Length > 0 ? _folderStore.Protect(PasswordBox.Password) : string.Empty;
            _folderSchedules.RemoveAll(each => each.Id == schedule.Id);
            _folderSchedules.Add(schedule);
            _folderStore.SaveSchedules(_folderSchedules);
            StatusLine.Text = schedule.Frequency == BackupFrequency.Live
                ? $"\"{schedule.Name}\" is saved; it copies everything new within a minute, then each change."
                : $"\"{schedule.Name}\" is saved; it next runs {BackupScheduling.NextRun(schedule, DateTime.Now):ddd dd MMM HH:mm}.";
        }
        else
        {
            if (BackupRunner.Validate(schedule) is { } problem)
            {
                WpfMessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!await SaveImageAsync(schedule, schedule.ShareUser.Length > 0 ? PasswordBox.Password : string.Empty))
            {
                return;
            }
        }

        Editor.Visibility = Visibility.Collapsed;
        _editing = null;
        Refresh();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Editor.Visibility = Visibility.Collapsed;
        _editing = null;
    }

    // ---- Restore and clone (administrator window) ----------------------------------------------------------------

    private void RestoreButton_Click(object sender, RoutedEventArgs e) => OpenCloneWindow(3);

    private void CloneButton_Click(object sender, RoutedEventArgs e) => OpenCloneWindow(0);

    private void PartitionButton_Click(object sender, RoutedEventArgs e) => OpenCloneWindow(1);

    private void ChosenPartitionsButton_Click(object sender, RoutedEventArgs e) => OpenCloneWindow(2);

    private void OpenCloneWindow(int tab)
    {
        if (PrivilegedOperations.IsAdministrator)
        {
            new DiskCloneWindow(tab) { Owner = this }.Show();
            return;
        }

        StatusLine.Text = ElevatedTool.Open(ElevatedTool.DiskClone, tab.ToString())
            ? "Opened in its own window as administrator."
            : "Not opened: the administrator prompt was cancelled.";
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

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

    /// <summary>A copy to edit, so a cancelled edit leaves the list as it was.</summary>
    private static BackupSchedule Copy(BackupSchedule schedule) =>
        JsonSerializer.Deserialize<BackupSchedule>(JsonSerializer.Serialize(schedule))!;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
