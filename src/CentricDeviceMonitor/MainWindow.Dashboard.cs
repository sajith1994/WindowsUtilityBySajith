using System.Windows;
using System.Windows.Threading;
using CentricDeviceMonitor.Controls;
using CentricDeviceMonitor.Dialogs;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor;

/// <summary>Customizable System Stats layout and desktop widgets.</summary>
public partial class MainWindow
{
    private readonly List<DesktopWidgetWindow> _desktopWidgets = new();
    private List<DashboardTileLayout> _defaultDashboardLayout = new();
    private DispatcherTimer? _layoutSaveTimer;
    private bool _dashboardEditing;

    private IEnumerable<DashboardTile> DashboardTiles => DashboardTilesPanel.Children.OfType<DashboardTile>();

    /// <summary>Called once settings are loaded: remembers the built-in layout, applies the saved one and wires tile events.</summary>
    private void InitializeDashboardLayout()
    {
        _defaultDashboardLayout = DashboardTiles
            .Select(tile => new DashboardTileLayout { Id = tile.TileId, Span = tile.Span, Height = null })
            .ToList();

        foreach (DashboardTile tile in DashboardTiles)
        {
            tile.LayoutChanged += (_, _) => ScheduleLayoutSave();
            tile.PinRequested += (sender, _) => PinTileToDesktop((DashboardTile)sender!);
            tile.CustomizeRequested += (_, _) => SetDashboardEditing(true);
        }

        ApplyDashboardLayout(_appSettings.DashboardLayout);
    }

    private void ApplyDashboardLayout(IReadOnlyList<DashboardTileLayout> layout)
    {
        Dictionary<string, DashboardTile> tiles = DashboardTiles.ToDictionary(tile => tile.TileId, StringComparer.OrdinalIgnoreCase);

        // Saved order first, then any tile added in a newer version in its built-in position.
        List<DashboardTile> ordered = new();
        foreach (DashboardTileLayout entry in layout)
        {
            if (tiles.Remove(entry.Id, out DashboardTile? tile))
            {
                tile.Span = Math.Clamp(entry.Span, 1, 4);
                tile.TileHeight = entry.Height is > 0 ? Math.Max(DashboardTile.MinimumTileHeight, entry.Height.Value) : double.NaN;
                ordered.Add(tile);
            }
        }

        foreach (DashboardTileLayout entry in _defaultDashboardLayout)
        {
            if (tiles.Remove(entry.Id, out DashboardTile? tile))
            {
                ordered.Add(tile);
            }
        }

        DashboardTilesPanel.Children.Clear();
        foreach (DashboardTile tile in ordered)
        {
            DashboardTilesPanel.Children.Add(tile);
        }
    }

    private void CustomizeDashboardButton_Click(object sender, RoutedEventArgs e) => SetDashboardEditing(!_dashboardEditing);

    private void SetDashboardEditing(bool editing)
    {
        _dashboardEditing = editing;
        foreach (DashboardTile tile in DashboardTiles)
        {
            tile.IsEditing = editing;
        }

        CustomizeDashboardButton.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        DoneCustomizingDashboardButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        ResetDashboardLayoutButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        DashboardLayoutHintText.Text = editing
            ? "Drag a tile to move it. Drag its blue corner to resize, or use its size button. Double-click the corner to fit the height."
            : "Right-click a tile to pin it to the desktop.";

        if (editing)
        {
            NavigationList.SelectedIndex = 0;
            StatusText.Text = "Customizing the dashboard. Changes are saved automatically.";
        }
        else
        {
            StatusText.Text = "Dashboard layout saved.";
        }
    }

    private void ResetDashboardLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (DashboardTileLayout entry in _defaultDashboardLayout)
        {
            entry.Height = null;
        }

        ApplyDashboardLayout(_defaultDashboardLayout);
        foreach (DashboardTile tile in DashboardTiles)
        {
            tile.TileHeight = double.NaN;
        }

        ScheduleLayoutSave();
        StatusText.Text = "Dashboard layout reset to the default.";
    }

    private void ScheduleLayoutSave()
    {
        // Widgets report every pixel of a move; save once things settle.
        if (_layoutSaveTimer is null)
        {
            _layoutSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _layoutSaveTimer.Tick += async (_, _) =>
            {
                _layoutSaveTimer.Stop();
                await SaveDashboardLayoutAsync();
            };
        }

        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
    }

    private async Task SaveDashboardLayoutAsync()
    {
        _appSettings.DashboardLayout = DashboardTiles
            .Select(tile => new DashboardTileLayout
            {
                Id = tile.TileId,
                Span = tile.Span,
                Height = double.IsNaN(tile.TileHeight) ? null : Math.Round(tile.TileHeight)
            })
            .ToList();
        _appSettings.DesktopWidgets = _desktopWidgets.Select(widget => widget.Layout).ToList();

        try
        {
            await _settingsService.SaveAsync(_appSettings);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Save dashboard layout", exception);
        }
    }

    private void PinTileToDesktop(DashboardTile tile)
    {
        DesktopWidgetWindow? existing = _desktopWidgets.FirstOrDefault(widget => widget.TileId == tile.TileId);
        if (existing is not null)
        {
            existing.Activate();
            StatusText.Text = $"{tile.Title} is already on the desktop.";
            return;
        }

        OpenDesktopWidget(tile, new DesktopWidgetLayout { Id = tile.TileId });
        ScheduleLayoutSave();
        StatusText.Text = $"{tile.Title} pinned to the desktop. Drag it anywhere; right-click it for options.";
    }

    private void OpenDesktopWidget(DashboardTile tile, DesktopWidgetLayout layout)
    {
        DesktopWidgetWindow widget = new(tile, layout);
        widget.LayoutChanged += (_, _) => ScheduleLayoutSave();
        widget.OpenDashboardRequested += (_, _) => ShowDashboardFromTray();
        widget.Closed += (_, _) =>
        {
            _desktopWidgets.Remove(widget);
            if (widget.RemovedByUser)
            {
                ScheduleLayoutSave();
            }
        };

        _desktopWidgets.Add(widget);
        widget.Show();
    }

    /// <summary>Re-creates the widgets that were on the desktop last time, once the tiles have a size.</summary>
    private void RestoreDesktopWidgets()
    {
        List<DesktopWidgetLayout> saved = _appSettings.DesktopWidgets.ToList();
        if (saved.Count == 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Dictionary<string, DashboardTile> tiles = DashboardTiles.ToDictionary(tile => tile.TileId, StringComparer.OrdinalIgnoreCase);
            foreach (DesktopWidgetLayout layout in saved)
            {
                if (tiles.TryGetValue(layout.Id, out DashboardTile? tile)
                    && _desktopWidgets.All(widget => widget.TileId != tile.TileId))
                {
                    OpenDesktopWidget(tile, layout);
                }
            }
        });
    }
}
