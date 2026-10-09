using System.Windows;
using System.Windows.Controls;

namespace CentricDeviceMonitor.Controls;

/// <summary>
/// Lays dashboard tiles out in rows on a grid of columns. Tile widths are stored in quarters of the
/// dashboard and mapped onto 4, 2 or 1 columns depending on the available width, so the same layout
/// works from wide monitors down to 1024x600 screens. Every tile in a row is stretched to the row's
/// height so rows stay tidy.
/// </summary>
public sealed class DashboardPanel : System.Windows.Controls.Panel
{
    public const double Gap = 16;

    /// <summary>Number of columns used by the last layout pass.</summary>
    public int Columns { get; private set; } = 4;

    /// <summary>Width of one column in the last layout pass.</summary>
    public double ColumnWidth { get; private set; } = 200;

    public static int ColumnsForWidth(double width) => width >= 1000 ? 4 : width >= 560 ? 2 : 1;

    public int EffectiveSpan(UIElement child)
    {
        int quarters = child is DashboardTile tile ? tile.Span : 4;
        int span = (int)Math.Ceiling(Math.Clamp(quarters, 1, 4) * Columns / 4.0);
        return Math.Clamp(span, 1, Columns);
    }

    /// <summary>Converts a pixel width into the nearest quarter span for the current column count.</summary>
    public int QuartersForWidth(double width)
    {
        int columns = (int)Math.Round((width + Gap) / (ColumnWidth + Gap));
        columns = Math.Clamp(columns, 1, Columns);
        return Math.Clamp((int)Math.Round(columns * 4.0 / Columns), 1, 4);
    }

    public void MoveTile(DashboardTile source, DashboardTile target, bool after)
    {
        if (ReferenceEquals(source, target))
        {
            return;
        }

        Children.Remove(source);
        int index = Children.IndexOf(target);
        Children.Insert(after ? index + 1 : index, source);
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 1200 : availableSize.Width;
        Columns = ColumnsForWidth(width);
        ColumnWidth = Math.Max(1, (width - Gap * (Columns - 1)) / Columns);

        double total = 0;
        foreach (List<(UIElement Child, int Column, int Span)> row in BuildRows())
        {
            double rowHeight = 0;
            foreach ((UIElement child, _, int span) in row)
            {
                double tileWidth = span * ColumnWidth + (span - 1) * Gap;
                double fixedHeight = child is DashboardTile { TileHeight: > 0 } tile ? tile.TileHeight : double.PositiveInfinity;
                child.Measure(new System.Windows.Size(tileWidth, fixedHeight));
                rowHeight = Math.Max(rowHeight, double.IsInfinity(fixedHeight) ? child.DesiredSize.Height : fixedHeight);
            }

            total += rowHeight + Gap;
        }

        return new System.Windows.Size(width, Math.Max(0, total - Gap));
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        double y = 0;
        foreach (List<(UIElement Child, int Column, int Span)> row in BuildRows())
        {
            double rowHeight = row.Max(item => item.Child is DashboardTile { TileHeight: > 0 } tile
                ? tile.TileHeight
                : item.Child.DesiredSize.Height);

            foreach ((UIElement child, int column, int span) in row)
            {
                double x = column * (ColumnWidth + Gap);
                double tileWidth = span * ColumnWidth + (span - 1) * Gap;
                child.Arrange(new Rect(x, y, tileWidth, rowHeight));
            }

            y += rowHeight + Gap;
        }

        return finalSize;
    }

    private IEnumerable<List<(UIElement Child, int Column, int Span)>> BuildRows()
    {
        List<(UIElement, int, int)> row = new();
        int used = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            int span = EffectiveSpan(child);
            if (used + span > Columns && row.Count > 0)
            {
                yield return row;
                row = new List<(UIElement, int, int)>();
                used = 0;
            }

            row.Add((child, used, span));
            used += span;
        }

        if (row.Count > 0)
        {
            yield return row;
        }
    }
}
