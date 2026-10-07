using System.Net;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Dialogs;

public partial class DeviceEditorWindow : Window
{
    private readonly DeviceEntry? _existing;

    public string DeviceName { get; private set; } = string.Empty;
    public string IpAddress { get; private set; } = string.Empty;

    public DeviceEditorWindow(DeviceEntry? existing = null)
    {
        InitializeComponent();
        _existing = existing;

        if (existing is not null)
        {
            Title = "Edit managed device";
            HeadingText.Text = "Edit managed device";
            NameTextBox.Text = existing.Name;
            IpTextBox.Text = existing.IpAddress;
        }

        // Keep the TextBoxes on WPF's default invariant InputLanguage. The application
        // registers a safe input-language source at startup for Windows custom locales
        // (LCID/LANGID 0x1000), so no per-control culture override is needed here.
        InputLanguageManager.SetRestoreInputLanguage(NameTextBox, false);
        InputLanguageManager.SetRestoreInputLanguage(IpTextBox, false);

        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        }));
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string name = NameTextBox.Text.Trim();
        string ip = IpTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ValidationText.Text = "Enter a device name.";
            NameTextBox.Focus();
            return;
        }

        if (!IPAddress.TryParse(ip, out IPAddress? parsed) ||
            parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            ValidationText.Text = "Enter a valid IPv4 address, for example 192.168.1.50.";
            IpTextBox.Focus();
            return;
        }

        DeviceName = name;
        IpAddress = parsed.ToString();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
