using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowsUtility.Services;
using WpfButton = System.Windows.Controls.Button;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace WindowsUtility.Dialogs;

/// <summary>
/// One window per subject (CPU & Cooling, Storage, Logs ...) that gathers everything related in tabs, instead of
/// spreading it over several pages and dialogs. Tabs can hold new panels or the content of an existing dialog
/// window, so those dialogs keep their own logic.
/// </summary>
public sealed class HubWindow : Window
{
    private readonly TabControl _tabs = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly List<Window> _embeddedWindows = new();
    private readonly List<IDisposable> _disposables = new();

    public HubWindow(string key, string glyph, string title, string subtitle)
    {
        Key = key;
        Title = title;
        Width = 1100;
        Height = 760;
        MinWidth = 720;
        MinHeight = 480;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PageBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        Grid root = new() { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        StackPanel header = new();
        StackPanel titleRow = new() { Orientation = WpfOrientation.Horizontal };
        TextBlock icon = new() { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 22, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
        titleRow.Children.Add(icon);
        titleRow.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        header.Children.Add(titleRow);
        TextBlock subtitleText = new() { Text = subtitle, Margin = new Thickness(32, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        subtitleText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        header.Children.Add(subtitleText);
        root.Children.Add(header);

        Grid.SetRow(_tabs, 1);
        root.Children.Add(_tabs);

        WpfButton close = new() { Content = "Close", Tag = "", Width = 100, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        close.SetResourceReference(StyleProperty, "IconButton");
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 2);
        root.Children.Add(close);

        Content = root;
        Closed += (_, _) => CleanUp();
    }

    public string Key { get; }

    public void AddTab(string header, UIElement content)
    {
        if (content is IDisposable disposable)
        {
            _disposables.Add(disposable);
        }

        _tabs.Items.Add(new TabItem { Header = header, Content = content });
    }

    /// <summary>
    /// Hosts an existing dialog's content in a tab. The dialog is shown once off-screen so its Loaded logic runs and
    /// it can own child dialogs, then hidden; its own Close button is hidden because the hub closes it instead.
    /// </summary>
    public void AddWindowTab(string header, Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        window.Hide();

        if (window.Content is not FrameworkElement content)
        {
            return;
        }

        // Bindings in the moved content (e.g. Disk tools' disk list) were resolved against the window's DataContext,
        // which the content no longer inherits once it leaves the window.
        object? dataContext = window.DataContext;
        window.Content = null;
        if (dataContext is not null)
        {
            content.DataContext = dataContext;
        }

        if (window.Resources.Count > 0 || window.Resources.MergedDictionaries.Count > 0)
        {
            content.Resources.MergedDictionaries.Add(window.Resources);
        }

        HideCloseButtons(content);
        _embeddedWindows.Add(window);
        _tabs.Items.Add(new TabItem { Header = header, Content = content });
    }

    public void SelectTab(int index)
    {
        if (index >= 0 && index < _tabs.Items.Count)
        {
            _tabs.SelectedIndex = index;
        }
    }

    public void SelectTab(string header)
    {
        for (int index = 0; index < _tabs.Items.Count; index++)
        {
            if (_tabs.Items[index] is TabItem { Header: string text } && text == header)
            {
                _tabs.SelectedIndex = index;
                return;
            }
        }
    }

    private static void HideCloseButtons(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is WpfButton { Content: string text } button && text.Trim() == "Close")
            {
                button.Visibility = Visibility.Collapsed;
            }

            if (child is DependencyObject element)
            {
                HideCloseButtons(element);
            }
        }
    }

    private void CleanUp()
    {
        foreach (IDisposable disposable in _disposables)
        {
            try { disposable.Dispose(); } catch (Exception exception) { ApplicationLogService.WriteException("Hub cleanup", exception); }
        }

        foreach (Window window in _embeddedWindows)
        {
            try { window.Close(); } catch (Exception exception) { ApplicationLogService.WriteException("Hub cleanup", exception); }
        }
    }
}
