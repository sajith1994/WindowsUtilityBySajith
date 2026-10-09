using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CentricDeviceMonitor.Controls;
using CentricDeviceMonitor.Models;
using WpfButton = System.Windows.Controls.Button;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfCursors = System.Windows.Input.Cursors;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace CentricDeviceMonitor.Dialogs;

/// <summary>
/// A borderless desktop widget that shows a live copy of one dashboard tile. The copy is drawn from the
/// tile itself (VisualBrush), so it updates whenever the dashboard updates the tile, including while the
/// dashboard is hidden in the notification area. It is a picture of the tile: buttons on it are not
/// clickable. Double-click opens the dashboard.
/// </summary>
public sealed class DesktopWidgetWindow : Window
{
    private readonly WpfButton _closeButton;

    public DesktopWidgetWindow(DashboardTile tile, DesktopWidgetLayout layout)
    {
        Tile = tile;
        Layout = layout;
        TileId = tile.TileId;

        Title = $"{tile.Title} widget";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        MinWidth = 160;
        MinHeight = 90;
        Topmost = layout.Topmost;
        WindowStartupLocation = WindowStartupLocation.Manual;

        FrameworkElement source = tile.Card ?? tile;
        double width = layout.Width > 0 ? layout.Width : Math.Clamp(source.ActualWidth, 220, 440);
        double aspect = source.ActualWidth > 0 && source.ActualHeight > 0 ? source.ActualHeight / source.ActualWidth : 0.6;
        Width = width;
        Height = layout.Height > 0 ? layout.Height : Math.Max(MinHeight, width * aspect);

        Rect workArea = SystemParameters.WorkArea;
        Left = layout.Width > 0 ? layout.Left : workArea.Right - Width - 24;
        Top = layout.Width > 0 ? layout.Top : workArea.Top + 24;
        KeepOnScreen();

        System.Windows.Shapes.Rectangle mirror = new()
        {
            Fill = new VisualBrush(source)
            {
                Stretch = Stretch.Uniform,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Top
            }
        };

        _closeButton = new WpfButton
        {
            Content = "",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            ToolTip = "Remove widget",
            Cursor = WpfCursors.Hand,
            Visibility = Visibility.Hidden
        };
        _closeButton.SetResourceReference(StyleProperty, "CompactButton");
        _closeButton.Click += (_, _) => RemoveByUser();

        Grid root = new() { Background = System.Windows.Media.Brushes.Transparent };
        root.Children.Add(mirror);
        root.Children.Add(_closeButton);
        Content = root;
        ContextMenu = BuildContextMenu();

        MouseEnter += (_, _) => _closeButton.Visibility = Visibility.Visible;
        MouseLeave += (_, _) => _closeButton.Visibility = Visibility.Hidden;
        MouseLeftButtonDown += Widget_MouseLeftButtonDown;
        LocationChanged += (_, _) => RaiseLayoutChanged();
        SizeChanged += (_, _) => RaiseLayoutChanged();
    }

    public DashboardTile Tile { get; }

    public DesktopWidgetLayout Layout { get; }

    public string TileId { get; }

    /// <summary>Moved, resized or pinned on top; the host saves the widget positions.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>The user asked to open the dashboard from the widget.</summary>
    public event EventHandler? OpenDashboardRequested;

    /// <summary>True when the user removed the widget, as opposed to the app shutting down.</summary>
    public bool RemovedByUser { get; private set; }

    public void RemoveByUser()
    {
        RemovedByUser = true;
        Close();
    }

    private void Widget_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OpenDashboardRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the move started.
        }
    }

    private WpfContextMenu BuildContextMenu()
    {
        WpfContextMenu menu = new();

        WpfMenuItem onTop = new() { Header = "Keep on top", IsCheckable = true, IsChecked = Topmost };
        onTop.Click += (_, _) =>
        {
            Topmost = onTop.IsChecked;
            RaiseLayoutChanged();
        };

        WpfMenuItem open = new() { Header = "Open dashboard" };
        open.Click += (_, _) => OpenDashboardRequested?.Invoke(this, EventArgs.Empty);

        WpfMenuItem remove = new() { Header = "Remove widget" };
        remove.Click += (_, _) => RemoveByUser();

        menu.Items.Add(onTop);
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        return menu;
    }

    private void RaiseLayoutChanged()
    {
        if (!IsLoaded)
        {
            return;
        }

        Layout.Left = Left;
        Layout.Top = Top;
        Layout.Width = ActualWidth;
        Layout.Height = ActualHeight;
        Layout.Topmost = Topmost;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pulls a saved position back onto the screen, e.g. after a monitor was disconnected.</summary>
    private void KeepOnScreen()
    {
        double left = SystemParameters.VirtualScreenLeft;
        double top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth;
        double bottom = top + SystemParameters.VirtualScreenHeight;
        Left = Math.Clamp(Left, left, Math.Max(left, right - Width));
        Top = Math.Clamp(Top, top, Math.Max(top, bottom - Height));
    }
}
