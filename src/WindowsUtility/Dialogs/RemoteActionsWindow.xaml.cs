using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using WindowsUtility.Models;
using WindowsUtility.Services;
using WpfMessageBox = System.Windows.MessageBox;

namespace WindowsUtility.Dialogs;

/// <summary>
/// Actions on a monitored PC that allows them: message, restart or shut down with a countdown (and cancel), lock,
/// clear temporary files, a Windows Update check and a restore point. Every action is confirmed here first and logged
/// on both PCs.
/// </summary>
public partial class RemoteActionsWindow : Window
{
    private readonly RemoteMonitorClient _client;
    private readonly RemotePcView _view;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;

    public RemoteActionsWindow(RemoteMonitorClient client, RemotePcView view)
    {
        InitializeComponent();
        _client = client;
        _view = view;
        Title = $"Actions: {view.Title}";
        HeadingText.Text = $"Actions: {view.Title}";
        SessionText.Text = $"{view.SessionsText}  •  every action is confirmed first and logged on both PCs";
        Closed += (_, _) => _lifetime.Cancel();
    }

    private async void SendMessageButton_Click(object sender, RoutedEventArgs e)
    {
        string message = MessageTextBox.Text.Trim();
        if (message.Length == 0)
        {
            ShowResult("Type the message first.", ok: false);
            MessageTextBox.Focus();
            return;
        }

        if (Confirm($"Show this message on {_view.Title}?\n\n\"{message}\""))
        {
            await RunAsync(new RemoteActionRequest { Action = RemoteActions.Message, Message = message });
        }
    }

    private async void RestartButton_Click(object sender, RoutedEventArgs e) => await PowerAsync(RemoteActions.Restart, "restart");

    private async void ShutdownButton_Click(object sender, RoutedEventArgs e) => await PowerAsync(RemoteActions.Shutdown, "shut down");

    private async Task PowerAsync(string action, string verb)
    {
        int minutes = DelayComboBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int value) ? value : 5;
        string when = minutes == 0 ? "now" : $"in {minutes} minute{(minutes == 1 ? "" : "s")}";
        string warning = _view.SomeoneActive
            ? $"\n\nSomeone is using {_view.Title} right now ({_view.SessionsText.Replace("Signed in: ", string.Empty)}). Unsaved work is lost if they do not save it in time."
            : string.Empty;
        if (!Confirm($"{char.ToUpperInvariant(verb[0])}{verb[1..]} {_view.Title} {when}?{warning}", warn: true))
        {
            return;
        }

        await RunAsync(new RemoteActionRequest
        {
            Action = action,
            DelayMinutes = minutes,
            Message = MessageTextBox.Text.Trim()
        });
    }

    private async void CancelShutdownButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(new RemoteActionRequest { Action = RemoteActions.CancelShutdown });

    private async void LockButton_Click(object sender, RoutedEventArgs e)
    {
        if (Confirm($"Lock the screen of {_view.Title}? The user signs in again with their password."))
        {
            await RunAsync(new RemoteActionRequest { Action = RemoteActions.Lock });
        }
    }

    private async void ClearTempButton_Click(object sender, RoutedEventArgs e)
    {
        if (Confirm($"Remove temporary files older than a day on {_view.Title}? Files in use are left alone."))
        {
            await RunAsync(new RemoteActionRequest { Action = RemoteActions.ClearTemp });
        }
    }

    private async void UpdateScanButton_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(new RemoteActionRequest { Action = RemoteActions.UpdateScan });

    private async void RestorePointButton_Click(object sender, RoutedEventArgs e)
    {
        if (Version.TryParse(_view.Stats?.AppVersion, out Version? version) && version < new Version(2, 0, 77))
        {
            ShowResult($"{_view.Title} has Windows Utility {version.ToString(3)}, which cannot create restore points yet. Update it first (Update on its tile).", ok: false);
            return;
        }

        if (Confirm($"Create a restore point on {_view.Title} now? It takes up to a few minutes and slows that PC a little meanwhile."))
        {
            await RunAsync(new RemoteActionRequest { Action = RemoteActions.RestorePoint });
        }
    }

    private bool Confirm(string question, bool warn = false) =>
        WpfMessageBox.Show(this, question, Title, MessageBoxButton.YesNo, warn ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes;

    private async Task RunAsync(RemoteActionRequest request)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        request.RequestedBy = Environment.MachineName;
        BusyBar.Visibility = Visibility.Visible;
        ShowResult("Sending...", ok: true);
        try
        {
            RemoteActionResult result = await _client.RunActionAsync(_view.Entry, request, _lifetime.Token);
            ShowResult(result.Message, result.Ok);
            ApplicationLogService.WriteMessage("Remote action", $"{request.Action} on {_view.Title} ({_view.Entry.Host}): {(result.Ok ? "done" : "failed")} - {result.Message}");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException)
        {
            string message = exception switch
            {
                TaskCanceledException => $"{_view.Title} did not answer in time.",
                HttpRequestException or IOException => $"Could not reach {_view.Title}. It may be off, asleep or no longer sharing.",
                _ => exception.Message
            };
            ShowResult(message, ok: false);
            ApplicationLogService.WriteMessage("Remote action", $"{request.Action} on {_view.Title} ({_view.Entry.Host}) failed: {message}");
        }
        finally
        {
            _busy = false;
            if (!_lifetime.IsCancellationRequested)
            {
                BusyBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ShowResult(string text, bool ok)
    {
        ResultText.Text = text;
        ResultText.SetResourceReference(TextBlock.ForegroundProperty, ok ? "TextBrush" : "DangerBrush");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
