using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.Dialogs;

/// <summary>Adds or edits a PC on the Network PCs page; Test connection reads its stats once before saving.</summary>
public partial class RemotePcEditorWindow : Window
{
    private readonly RemoteMonitorClient _client;

    public RemotePcEntry Result { get; }

    /// <param name="groups">Groups already used on the Network PCs page, offered in the Group list.</param>
    public RemotePcEditorWindow(RemoteMonitorClient client, RemotePcEntry? existing = null, string? host = null, string? name = null, string? code = null,
        IEnumerable<string>? groups = null)
    {
        InitializeComponent();
        _client = client;
        Result = existing is null
            ? new RemotePcEntry()
            : new RemotePcEntry
            {
                Id = existing.Id,
                DisplayName = existing.DisplayName,
                Host = existing.Host,
                Port = existing.Port,
                PairingCode = existing.PairingCode,
                Group = existing.Group,
                WakeMacAddresses = existing.WakeMacAddresses.ToList(),
                WakeOnLanEnabled = existing.WakeOnLanEnabled,
                LastKnownAddress = existing.LastKnownAddress
            };
        GroupComboBox.ItemsSource = (groups ?? Enumerable.Empty<string>()).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase).ToList();
        GroupComboBox.Text = existing?.Group ?? string.Empty;

        if (existing is not null)
        {
            Title = "Edit monitored PC";
            HeadingText.Text = "Edit monitored PC";
            SaveButton.Content = "Save changes";
        }

        NameTextBox.Text = existing?.DisplayName ?? name ?? string.Empty;
        HostTextBox.Text = existing?.Host ?? host ?? string.Empty;
        PortTextBox.Text = (existing?.Port ?? RemoteMonitoringProtocol.DefaultPort).ToString();
        CodeTextBox.Text = existing?.PairingCode ?? code ?? string.Empty;

        InputLanguageManager.SetRestoreInputLanguage(NameTextBox, false);
        InputLanguageManager.SetRestoreInputLanguage(HostTextBox, false);
        InputLanguageManager.SetRestoreInputLanguage(PortTextBox, false);
        InputLanguageManager.SetRestoreInputLanguage(CodeTextBox, false);

        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            // Coming from Find PCs the address is filled in, so the code is what is left to type.
            System.Windows.Controls.TextBox first = string.IsNullOrWhiteSpace(HostTextBox.Text) ? HostTextBox : CodeTextBox;
            first.Focus();
            first.SelectAll();
        }));
    }

    private bool TryReadFields(out string host, out int port, out string code)
    {
        host = HostTextBox.Text.Trim().TrimStart('\\').TrimEnd('/');
        code = CodeTextBox.Text.Trim();
        port = 0;
        if (host.Length == 0 || host.Any(char.IsWhiteSpace))
        {
            ShowResult("Enter the PC's name or IP address, for example 192.168.1.50.", "DangerBrush");
            HostTextBox.Focus();
            return false;
        }

        if (!int.TryParse(PortTextBox.Text.Trim(), out port) || port is < 1024 or > 65535)
        {
            ShowResult("Enter a port between 1024 and 65535 (normally 47810).", "DangerBrush");
            PortTextBox.Focus();
            return false;
        }

        if (RemoteMonitoringProtocol.NormalizeCode(code).Length < RemoteMonitoringProtocol.MinimumCodeLength)
        {
            ShowResult("Enter the pairing code shown on that PC (for example ABCD-EFGH-JKLM).", "DangerBrush");
            CodeTextBox.Focus();
            return false;
        }

        return true;
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadFields(out string host, out int port, out string code))
        {
            return;
        }

        TestButton.IsEnabled = false;
        ShowResult($"Connecting to {host}...", "MutedTextBrush");
        try
        {
            RemoteStatsResult result = await _client.GetStatsAsync(host, port, code);
            if (result.State == RemoteStatsState.Online && result.Stats is { } stats)
            {
                if (string.IsNullOrWhiteSpace(NameTextBox.Text))
                {
                    NameTextBox.Text = stats.MachineName;
                }

                ShowResult($"Connected to {stats.MachineName} ({stats.OsDisplay}). CPU {stats.CpuUsagePercent:0}%, RAM {stats.RamUsagePercent:0}%.", "SuccessBrush");
            }
            else
            {
                ShowResult(result.Message, result.State == RemoteStatsState.WrongCode ? "WarningBrush" : "DangerBrush");
            }
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadFields(out string host, out int port, out string code))
        {
            return;
        }

        Result.DisplayName = NameTextBox.Text.Trim();
        Result.Group = GroupComboBox.Text.Trim();
        if (!string.Equals(Result.Host, host, StringComparison.OrdinalIgnoreCase))
        {
            // Another PC (or the same PC by another name): its wake details are learned again.
            Result.WakeMacAddresses = new List<string>();
            Result.WakeOnLanEnabled = null;
            Result.LastKnownAddress = string.Empty;
        }

        Result.Host = host;
        Result.Port = port;
        Result.PairingCode = code;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowResult(string text, string brushKey)
    {
        ResultText.Text = text;
        ResultText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, brushKey);
    }
}
