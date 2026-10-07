using System.IO;
using System.Windows;

namespace CentricDeviceMonitor.Dialogs;

public partial class TermsWindow : Window
{
    private const string TermsFileName = "TERMS-AND-CONDITIONS.txt";

    public TermsWindow()
    {
        InitializeComponent();
        TermsTextBox.Text = LoadTermsText();
    }

    private static string LoadTermsText()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, TermsFileName);
            return File.Exists(path)
                ? File.ReadAllText(path)
                : "The Terms & Conditions file could not be found. Please reinstall or repair Windows Utility by Sajith.";
        }
        catch (Exception exception)
        {
            return $"The Terms & Conditions could not be loaded.\n\n{exception.Message}";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
