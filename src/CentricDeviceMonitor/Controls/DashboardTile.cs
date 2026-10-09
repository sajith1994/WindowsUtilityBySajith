using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfCursors = System.Windows.Input.Cursors;
using WpfDataObject = System.Windows.DataObject;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfPoint = System.Windows.Point;

namespace CentricDeviceMonitor.Controls;

/// <summary>
/// One movable, resizable dashboard tile. The XAML child is the tile's card; in edit mode an overlay
/// covers it with a drag handle, size menu, pin-to-desktop button and a corner resize grip, and blocks
/// clicks from reaching the card's own buttons.
/// </summary>
public sealed class DashboardTile : Grid
{
    public const double MinimumTileHeight = 90;

    public static readonly DependencyProperty SpanProperty = DependencyProperty.Register(
        nameof(Span), typeof(int), typeof(DashboardTile),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(
        nameof(TileHeight), typeof(double), typeof(DashboardTile),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
        nameof(IsEditing), typeof(bool), typeof(DashboardTile),
        new PropertyMetadata(false, (d, _) => ((DashboardTile)d).UpdateEditVisuals()));

    private readonly Border _overlay = new();
    private readonly ScrollViewer _scroller = new();
    private WpfPoint? _dragStart;
    private Vector _resizeOffset;
    private bool _dropTarget;

    /// <summary>Quarter-width units (1 to 4).</summary>
    public int Span
    {
        get => (int)GetValue(SpanProperty);
        set => SetValue(SpanProperty, Math.Clamp(value, 1, 4));
    }

    /// <summary>Fixed height in pixels, or NaN to fit the content.</summary>
    public double TileHeight
    {
        get => (double)GetValue(TileHeightProperty);
        set => SetValue(TileHeightProperty, value);
    }

    public bool IsEditing
    {
        get => (bool)GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    public string TileId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>The tile's card, used as the live source for desktop widgets.</summary>
    public FrameworkElement? Card { get; private set; }

    /// <summary>Order or size changed by the user; the host saves the layout.</summary>
    public event EventHandler? LayoutChanged;

    public event EventHandler? PinRequested;

    public event EventHandler? CustomizeRequested;

    private DashboardPanel? Host => Parent as DashboardPanel;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);

        // XAML supplies the card as the only child; host it in a scroller so a fixed height never hides content.
        if (Children.Count > 0 && Children[0] is FrameworkElement card)
        {
            Children.Remove(card);
            card.Margin = new Thickness(0);
            card.VerticalAlignment = VerticalAlignment.Stretch;
            Card = card;
            _scroller.Content = card;
        }

        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroller.Focusable = false;
        _scroller.PreviewMouseWheel += Scroller_PreviewMouseWheel;
        Children.Add(_scroller);

        BuildOverlay();
        Children.Add(_overlay);
        ContextMenu = BuildContextMenu();
        AllowDrop = true;
        UpdateEditVisuals();
    }

    private void BuildOverlay()
    {
        _overlay.CornerRadius = new CornerRadius(14);
        _overlay.BorderThickness = new Thickness(2);
        _overlay.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
        _overlay.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x22, 0x00, 0x7A, 0xFF));
        _overlay.Cursor = WpfCursors.SizeAll;
        _overlay.ToolTip = "Drag to move. Drag the corner to resize.";
        _overlay.MouseLeftButtonDown += Overlay_MouseLeftButtonDown;
        _overlay.MouseMove += Overlay_MouseMove;
        _overlay.MouseLeftButtonUp += (_, _) => _dragStart = null;

        Grid layout = new();

        Border header = new()
        {
            CornerRadius = new CornerRadius(12, 12, 0, 0),
            Padding = new Thickness(10, 6, 6, 6),
            VerticalAlignment = VerticalAlignment.Top
        };
        header.SetResourceReference(Border.BackgroundProperty, "CardBrush");

        DockPanel headerContent = new() { LastChildFill = true };
        StackPanel buttons = new() { Orientation = WpfOrientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(CreateHeaderButton("", "Resize", (sender, _) => OpenSizeMenu((FrameworkElement)sender)));
        buttons.Children.Add(CreateHeaderButton("", "Pin to desktop as a widget", (_, _) => PinRequested?.Invoke(this, EventArgs.Empty)));
        headerContent.Children.Add(buttons);

        StackPanel titlePanel = new() { Orientation = WpfOrientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titlePanel.Children.Add(Glyph("", 14, "PrimaryBrush", new Thickness(0, 0, 8, 0)));
        TextBlock title = new() { Text = Title, FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Title)) { Source = this });
        titlePanel.Children.Add(title);
        headerContent.Children.Add(titlePanel);
        header.Child = headerContent;
        layout.Children.Add(header);

        Thumb grip = new()
        {
            Width = 22,
            Height = 22,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 4),
            Cursor = WpfCursors.SizeNWSE,
            ToolTip = "Drag to resize. Double-click to fit the height to the content.",
            Template = CreateGripTemplate()
        };
        grip.DragStarted += Grip_DragStarted;
        grip.DragDelta += Grip_DragDelta;
        grip.DragCompleted += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
        grip.MouseDoubleClick += (_, args) =>
        {
            TileHeight = double.NaN;
            LayoutChanged?.Invoke(this, EventArgs.Empty);
            args.Handled = true;
        };
        layout.Children.Add(grip);

        _overlay.Child = layout;
    }

    private static ControlTemplate CreateGripTemplate()
    {
        FrameworkElementFactory border = new(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetResourceReference(Border.BackgroundProperty, "PrimaryBrush");
        FrameworkElementFactory glyph = new(typeof(TextBlock));
        glyph.SetValue(TextBlock.TextProperty, "");
        glyph.SetValue(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        glyph.SetValue(TextBlock.FontSizeProperty, 11.0);
        glyph.SetValue(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.White);
        glyph.SetValue(HorizontalAlignmentProperty, WpfHorizontalAlignment.Center);
        glyph.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(glyph);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = border };
    }

    private WpfButton CreateHeaderButton(string glyph, string tooltip, RoutedEventHandler click)
    {
        WpfButton button = new()
        {
            Content = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            Width = 30,
            Height = 26,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(0),
            ToolTip = tooltip,
            Cursor = WpfCursors.Hand
        };
        button.SetResourceReference(StyleProperty, "CompactButton");
        button.Click += click;
        return button;
    }

    private static TextBlock Glyph(string glyph, double size, string brushKey, Thickness margin)
    {
        TextBlock text = new()
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = size,
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return text;
    }

    private WpfContextMenu BuildContextMenu()
    {
        WpfContextMenu menu = new();
        WpfMenuItem pin = new() { Header = "Pin to desktop as a widget" };
        pin.Click += (_, _) => PinRequested?.Invoke(this, EventArgs.Empty);
        WpfMenuItem customize = new() { Header = "Customize dashboard" };
        customize.Click += (_, _) => CustomizeRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(pin);
        menu.Items.Add(customize);
        return menu;
    }

    private void OpenSizeMenu(FrameworkElement placementTarget)
    {
        WpfContextMenu menu = new() { PlacementTarget = placementTarget, Placement = PlacementMode.Bottom };
        AddSizeItem(menu, "Quarter width", () => Span = 1, Span == 1);
        AddSizeItem(menu, "Half width", () => Span = 2, Span == 2);
        AddSizeItem(menu, "Three-quarter width", () => Span = 3, Span == 3);
        AddSizeItem(menu, "Full width", () => Span = 4, Span == 4);
        menu.Items.Add(new Separator());
        AddSizeItem(menu, "Fit height to content", () => TileHeight = double.NaN, double.IsNaN(TileHeight));
        AddSizeItem(menu, "Taller", () => TileHeight = CurrentHeight() + 60, false);
        AddSizeItem(menu, "Shorter", () => TileHeight = Math.Max(MinimumTileHeight, CurrentHeight() - 60), false);
        menu.IsOpen = true;
    }

    private void AddSizeItem(WpfContextMenu menu, string header, Action apply, bool isChecked)
    {
        WpfMenuItem item = new() { Header = header, IsCheckable = false, IsChecked = isChecked };
        item.Click += (_, _) =>
        {
            apply();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        };
        menu.Items.Add(item);
    }

    private double CurrentHeight() => double.IsNaN(TileHeight) ? ActualHeight : TileHeight;

    private void UpdateEditVisuals()
    {
        _overlay.Visibility = IsEditing ? Visibility.Visible : Visibility.Collapsed;
        _overlay.BorderThickness = new Thickness(_dropTarget ? 4 : 2);
    }

    private void Grip_DragStarted(object sender, DragStartedEventArgs e)
    {
        WpfPoint mouse = Mouse.GetPosition(this);
        _resizeOffset = new Vector(ActualWidth - mouse.X, ActualHeight - mouse.Y);
    }

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        WpfPoint mouse = Mouse.GetPosition(this);
        double width = Math.Max(60, mouse.X + _resizeOffset.X);
        double height = Math.Max(MinimumTileHeight, mouse.Y + _resizeOffset.Y);
        if (Host is DashboardPanel host)
        {
            Span = host.QuartersForWidth(width);
        }

        TileHeight = Math.Round(height);
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
    }

    private void Overlay_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_dragStart is not WpfPoint start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Vector moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragStart = null;
        Opacity = 0.55;
        try
        {
            System.Windows.DragDrop.DoDragDrop(this, new WpfDataObject(typeof(DashboardTile), this), WpfDragDropEffects.Move);
        }
        finally
        {
            Opacity = 1;
        }
    }

    protected override void OnDragOver(WpfDragEventArgs e)
    {
        base.OnDragOver(e);
        bool valid = IsEditing && e.Data.GetData(typeof(DashboardTile)) is DashboardTile source && !ReferenceEquals(source, this);
        e.Effects = valid ? WpfDragDropEffects.Move : WpfDragDropEffects.None;
        SetDropTarget(valid);
        e.Handled = true;
    }

    protected override void OnDragLeave(WpfDragEventArgs e)
    {
        base.OnDragLeave(e);
        SetDropTarget(false);
    }

    protected override void OnDrop(WpfDragEventArgs e)
    {
        base.OnDrop(e);
        SetDropTarget(false);
        if (!IsEditing || e.Data.GetData(typeof(DashboardTile)) is not DashboardTile source || Host is not DashboardPanel host)
        {
            return;
        }

        // Dropping on the right half places the tile after this one, otherwise before it.
        bool after = e.GetPosition(this).X > ActualWidth / 2;
        host.MoveTile(source, this, after);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void SetDropTarget(bool value)
    {
        if (_dropTarget != value)
        {
            _dropTarget = value;
            UpdateEditVisuals();
        }
    }

    /// <summary>Lets the page scroll when the mouse is over a tile that has nothing to scroll itself.</summary>
    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        bool canScroll = _scroller.ScrollableHeight > 0
            && !(e.Delta > 0 && _scroller.VerticalOffset <= 0)
            && !(e.Delta < 0 && _scroller.VerticalOffset >= _scroller.ScrollableHeight);
        if (canScroll)
        {
            return;
        }

        e.Handled = true;
        MouseWheelEventArgs forwarded = new(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = this
        };
        RaiseEvent(forwarded);
    }
}
