using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using CentricDeviceMonitor.Services;
using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;

namespace CentricDeviceMonitor.Dialogs;

public partial class WindowsRepairWindow : Window
{
    private static readonly Regex DriveLetterRegex = new(@"^[A-Za-z]:$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly WindowsUtilityService _windowsUtilityService = new();

    public WindowsRepairWindow()
    {
        InitializeComponent();

        string systemDrive = NormalizeDriveLetter(Environment.GetEnvironmentVariable("SystemDrive")) ?? "C:";
        ChkdskDriveTextBox.Text = systemDrive;
        OfflineWindowsDriveTextBox.Text = systemDrive;

        BootRecAvailabilityText.Text = _windowsUtilityService.IsBootRecAvailable()
            ? "BootRec is available in this environment. Confirm that you are repairing the intended Windows installation before changing boot records."
            : "BootRec.exe is not available in this Windows session. BootRec is normally supplied by Windows Recovery Environment (WinRE). If you click a BootRec shortcut here, the command will be copied so it can be used from WinRE.";

        Loaded += async (_, _) => await TryDetectWindowsDriveAsync(showMessageOnFailure: false);
    }

    private void SfcOnlineButton_Click(object sender, RoutedEventArgs e) =>
        RunElevatedCommand("sfc /scannow", "SFC system file scan");

    private void DismOnlineCheckButton_Click(object sender, RoutedEventArgs e) =>
        RunElevatedCommand("DISM.exe /Online /Cleanup-Image /CheckHealth", "DISM online CheckHealth");

    private void DismOnlineScanButton_Click(object sender, RoutedEventArgs e) =>
        RunElevatedCommand("DISM.exe /Online /Cleanup-Image /ScanHealth", "DISM online ScanHealth");

    private void DismOnlineRestoreButton_Click(object sender, RoutedEventArgs e) =>
        RunElevatedCommand("DISM.exe /Online /Cleanup-Image /RestoreHealth", "DISM online RestoreHealth");

    private void ChkdskButton_Click(object sender, RoutedEventArgs e)
    {
        string? drive = GetValidatedDrive(ChkdskDriveTextBox.Text, "CHKDSK");
        if (drive is null)
        {
            return;
        }

        MessageBoxResult result = WpfMessageBox.Show(
            this,
            $"Run CHKDSK {drive} /f /r?\n\nThis checks the full volume, attempts to repair file-system errors and scans for bad sectors. It may take a long time. If {drive} is in use, Windows may ask to schedule the repair for the next restart.",
            "Run CHKDSK",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        RunElevatedCommand($"chkdsk {drive} /f /r", $"CHKDSK {drive}");
    }

    private async void DetectWindowsDriveButton_Click(object sender, RoutedEventArgs e) =>
        await TryDetectWindowsDriveAsync(showMessageOnFailure: true);

    private void DismOfflineCheckButton_Click(object sender, RoutedEventArgs e) =>
        RunOfflineDism("/CheckHealth", "DISM offline CheckHealth");

    private void DismOfflineScanButton_Click(object sender, RoutedEventArgs e) =>
        RunOfflineDism("/ScanHealth", "DISM offline ScanHealth");

    private void DismOfflineRestoreButton_Click(object sender, RoutedEventArgs e) =>
        RunOfflineDism("/RestoreHealth", "DISM offline RestoreHealth");

    private void SfcOfflineButton_Click(object sender, RoutedEventArgs e)
    {
        string? drive = GetOfflineWindowsDrive();
        if (drive is null)
        {
            return;
        }

        string root = drive + "\\";
        RunElevatedCommand($"sfc /scannow /offbootdir={root} /offwindir={root}Windows", $"Offline SFC on {drive}");
    }

    private void OfflineRepairSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        string? drive = GetOfflineWindowsDrive();
        if (drive is null)
        {
            return;
        }

        MessageBoxResult result = WpfMessageBox.Show(
            this,
            $"Run the full offline repair sequence against Windows on {drive}?\n\nThe sequence runs DISM CheckHealth, ScanHealth, RestoreHealth, then SFC. This can take a significant amount of time. Confirm that {drive} is the offline Windows installation you intend to repair.",
            "Offline Windows repair",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        string root = drive + "\\";
        string command = string.Join("\r\n", new[]
        {
            $"echo === DISM CheckHealth ({drive}) ===",
            $"DISM.exe /Image:{root} /Cleanup-Image /CheckHealth",
            $"echo === DISM ScanHealth ({drive}) ===",
            $"DISM.exe /Image:{root} /Cleanup-Image /ScanHealth",
            $"echo === DISM RestoreHealth ({drive}) ===",
            $"DISM.exe /Image:{root} /Cleanup-Image /RestoreHealth",
            $"echo === Offline SFC ({drive}) ===",
            $"sfc /scannow /offbootdir={root} /offwindir={root}Windows"
        });

        RunElevatedCommand(command, $"Offline DISM + SFC sequence on {drive}");
    }

    private void BootRecFixMbrButton_Click(object sender, RoutedEventArgs e) =>
        RunBootRecCommand("bootrec /fixmbr", "Fix master boot record");

    private void BootRecFixBootButton_Click(object sender, RoutedEventArgs e) =>
        RunBootRecCommand("bootrec /fixboot", "Fix boot sector");

    private void BootRecRebuildBcdButton_Click(object sender, RoutedEventArgs e) =>
        RunBootRecCommand("bootrec /rebuildbcd", "Rebuild Boot Configuration Data");

    private void BootRecSequenceButton_Click(object sender, RoutedEventArgs e)
    {
        const string command = "bootrec /fixmbr\r\nbootrec /fixboot\r\nbootrec /rebuildbcd";
        RunBootRecCommand(command, "BootRec repair sequence", isSequence: true);
    }

    private void RunOfflineDism(string operation, string title)
    {
        string? drive = GetOfflineWindowsDrive();
        if (drive is null)
        {
            return;
        }

        string root = drive + "\\";
        RunElevatedCommand($"DISM.exe /Image:{root} /Cleanup-Image {operation}", $"{title} on {drive}");
    }

    private string? GetOfflineWindowsDrive()
    {
        string? drive = GetValidatedDrive(OfflineWindowsDriveTextBox.Text, "Offline Windows repair");
        if (drive is null)
        {
            return null;
        }

        string windowsPath = Path.Combine(drive + "\\", "Windows");
        if (!Directory.Exists(windowsPath))
        {
            MessageBoxResult result = WpfMessageBox.Show(
                this,
                $"A Windows folder was not found at {windowsPath}.\n\nContinue anyway? Only continue if you are certain that {drive} is the correct offline Windows volume.",
                "Windows folder not detected",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                return null;
            }
        }

        return drive;
    }

    private static string? NormalizeDriveLetter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim().TrimEnd('\\', '/').ToUpperInvariant();
        return DriveLetterRegex.IsMatch(normalized) ? normalized : null;
    }

    private string? GetValidatedDrive(string value, string title)
    {
        string? drive = NormalizeDriveLetter(value);
        if (drive is not null)
        {
            return drive;
        }

        WpfMessageBox.Show(
            this,
            "Enter a drive letter in the form C: or D:. Paths, spaces and command characters are not accepted.",
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return null;
    }

    private async Task TryDetectWindowsDriveAsync(bool showMessageOnFailure)
    {
        try
        {
            DetectedDriveStatusText.Text = "Detecting the Windows OS device from BCD...";
            string? drive = await _windowsUtilityService.TryDetectBcdOsDriveAsync();
            if (drive is null)
            {
                DetectedDriveStatusText.Text = "BCD did not return an OS drive. Enter the Windows drive manually.";
                if (showMessageOnFailure)
                {
                    WpfMessageBox.Show(this, "Windows Utility could not determine the OS drive from BCDEdit. Enter the drive shown by 'bcdedit | find \"osdevice\"' manually.", "Detect Windows drive", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            OfflineWindowsDriveTextBox.Text = drive;
            DetectedDriveStatusText.Text = $"BCDEdit reports the OS device as {drive}. Confirm that this is the Windows installation you intend to repair.";
            StatusText.Text = $"Detected Windows OS device: {drive}";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Detect Windows drive for repair", exception);
            DetectedDriveStatusText.Text = "Unable to detect the OS drive automatically. Enter it manually.";
            if (showMessageOnFailure)
            {
                WpfMessageBox.Show(this, exception.Message, "Detect Windows drive", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void RunBootRecCommand(string command, string title, bool isSequence = false)
    {
        if (!_windowsUtilityService.IsBootRecAvailable())
        {
            string clipboardStatus;
            try
            {
                WpfClipboard.SetText(command.Replace("\r\n", Environment.NewLine, StringComparison.Ordinal));
                clipboardStatus = $"The {(isSequence ? "commands were" : "command was")} copied to the clipboard for use in WinRE.";
                StatusText.Text = "BootRec is not available in this Windows session. Command copied for use in WinRE.";
            }
            catch (Exception exception)
            {
                ApplicationLogService.WriteException("Copy BootRec command", exception);
                clipboardStatus = $"Clipboard access failed. Use this {(isSequence ? "sequence" : "command")} manually in WinRE:\n\n{command}";
                StatusText.Text = "BootRec is not available in this Windows session.";
            }

            WpfMessageBox.Show(
                this,
                $"BootRec.exe is not available in this Windows session. It is normally run from Windows Recovery Environment.\n\n{clipboardStatus}",
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult result = WpfMessageBox.Show(
            this,
            $"{title} changes Windows boot information. Run only when repairing the intended Windows installation from the recovery environment.\n\nContinue?",
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        RunElevatedCommand(command, title);
    }

    private void RunElevatedCommand(string command, string title)
    {
        try
        {
            _windowsUtilityService.RunCommandPromptCommand(command, elevated: true);
            StatusText.Text = $"Opened {title} in Administrator Command Prompt.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException($"Windows repair - {title}", exception);
            WpfMessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
