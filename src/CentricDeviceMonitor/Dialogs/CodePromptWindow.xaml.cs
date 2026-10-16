using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace CentricDeviceMonitor.Dialogs;

/// <summary>
/// Asks for a pairing code. Masked mode (a password box) is used to unlock a hidden code; <paramref name="validate"/>
/// returns an error message, or null when the code is accepted.
/// </summary>
public partial class CodePromptWindow : Window
{
    private readonly Func<string, string?> _validate;
    private readonly bool _masked;

    public string Code { get; private set; } = string.Empty;

    /// <summary>True when the user chose "Forgot it? Set a new code" instead of entering the code.</summary>
    public bool ForgotRequested { get; private set; }

    public CodePromptWindow(string title, string heading, string message, string okText, Func<string, string?> validate,
        bool masked = false, string initialCode = "", bool offerForgot = false)
    {
        InitializeComponent();
        _validate = validate;
        _masked = masked;
        Title = title;
        HeadingText.Text = heading;
        MessageText.Text = message;
        OkButton.Content = okText;
        ForgotButton.Visibility = offerForgot ? Visibility.Visible : Visibility.Collapsed;
        CodeTextBox.Visibility = masked ? Visibility.Collapsed : Visibility.Visible;
        CodePasswordBox.Visibility = masked ? Visibility.Visible : Visibility.Collapsed;
        CodeTextBox.Text = initialCode;
        InputLanguageManager.SetRestoreInputLanguage(CodeTextBox, false);

        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (masked)
            {
                CodePasswordBox.Focus();
            }
            else
            {
                CodeTextBox.Focus();
                CodeTextBox.SelectAll();
            }
        }));
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        string code = (_masked ? CodePasswordBox.Password : CodeTextBox.Text).Trim();
        string? error = _validate(code);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            if (_masked)
            {
                CodePasswordBox.Clear();
                CodePasswordBox.Focus();
            }
            else
            {
                CodeTextBox.Focus();
                CodeTextBox.SelectAll();
            }

            return;
        }

        Code = code;
        DialogResult = true;
    }

    private void ForgotButton_Click(object sender, RoutedEventArgs e)
    {
        ForgotRequested = true;
        DialogResult = false;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
