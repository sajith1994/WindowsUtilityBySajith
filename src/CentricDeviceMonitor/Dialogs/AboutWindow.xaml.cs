using System.Reflection;
using System.Windows;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.Dialogs;

public partial class AboutWindow : Window
{
    private readonly AppSettings _settings;
    private readonly AppSettingsService _settingsService;

    public AboutWindow(AppSettings settings, AppSettingsService settingsService)
    {
        _settings = settings;
        _settingsService = settingsService;
        InitializeComponent();
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        DateTime? published = UpdateService.GetCurrentReleaseDate();
        string versionNumber = version is null ? "unavailable" : $"{version.Major}.{version.Minor}.{version.Build}";
        string publishedText = published is null ? string.Empty : $"  •  Published {UpdateService.FormatReleaseDate(published.Value)}";
        VersionText.Text = $"Version {versionNumber}{publishedText}  •  Developed by {UpdateService.DeveloperName}";
        VersionValueText.Text = published is null ? versionNumber : $"{versionNumber} (published {UpdateService.FormatReleaseDate(published.Value)})";
    }

    private void TermsButton_Click(object sender, RoutedEventArgs e)
    {
        TermsWindow termsWindow = new() { Owner = this };
        termsWindow.ShowDialog();
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateWindow updateWindow = new(_settings, _settingsService) { Owner = this };
        updateWindow.ShowDialog();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
