using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace CentricDeviceMonitor.Controls;

/// <summary>One plotted measurement. Values line up with <see cref="LiveChart"/>'s shared time stamps.</summary>
public sealed class LiveChartSeries
{
    public LiveChartSeries(string name, string unit, WpfColor color, double? fixedMin = null, double? fixedMax = null)
    {
        Name = name;
        Unit = unit;
        Color = color;
        FixedMin = fixedMin;
        FixedMax = fixedMax;
    }

    public string Name { get; }
    public string Unit { get; }
    public WpfColor Color { get; }

    /// <summary>Percentages use a fixed 0-100 axis so the shape is not exaggerated.</summary>
    public double? FixedMin { get; }
    public double? FixedMax { get; }

    public List<double?> Values { get; } = new();
    public bool IsVisible { get; set; } = true;

    /// <summary>Optional limit drawn as a dashed red line across the lane (e.g. a thermal stop temperature).</summary>
    public double? Threshold { get; set; }
    public bool HasData => Values.Any(value => value.HasValue);

    public string Format(double? value) => value.HasValue ? $"{LiveChart.FormatNumber(value.Value)} {Unit}" : "no reading";
}

/// <summary>
/// Modern live chart: one lane per visible measurement, each with its own labelled Y axis (value + unit), a shared
/// clock-time X axis with gridlines, smooth area-filled lines, a hover crosshair with a value read-out, and a
/// point that can be pinned by clicking (right-click or Esc clears it).
/// </summary>
public sealed class LiveChart : FrameworkElement
{
    private const double AxisWidth = 64;
    private const double RightPadding = 14;
    private const double TimeAxisHeight = 34;
    private const double LaneGap = 14;
    private const double LaneHeaderHeight = 20;

    private IReadOnlyList<DateTime> _times = Array.Empty<DateTime>();
    private IReadOnlyList<LiveChartSeries> _series = Array.Empty<LiveChartSeries>();
    private TimeSpan _range = TimeSpan.FromMinutes(1);
    private WpfPoint? _hover;
    private DateTime? _pinned;

    public LiveChart()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        MinHeight = 160;
    }

    /// <summary>Raised when the user pins a point (time and the values of every visible series), or null when cleared.</summary>
    public event EventHandler<string?>? PointSelected;

    /// <summary>Shown when there is nothing to plot yet.</summary>
    public string EmptyText { get; set; } = "Collecting samples...";

    /// <summary>Hours and days (history graphs) rather than seconds: point times also show the day.</summary>
    private bool LongRange => _range > TimeSpan.FromHours(2);

    private string PointTimeFormat => LongRange ? "ddd d MMM HH:mm" : "HH:mm:ss";

    public void SetData(IReadOnlyList<DateTime> times, IReadOnlyList<LiveChartSeries> series, TimeSpan range)
    {
        _times = times;
        _series = series;
        _range = range;
        InvalidateVisual();
    }

    private IReadOnlyList<(DateTime Time, string Label)> _markers = Array.Empty<(DateTime, string)>();

    /// <summary>Labelled vertical markers across every lane, e.g. "Test started" / "Safety stop".</summary>
    public void SetMarkers(IReadOnlyList<(DateTime Time, string Label)> markers)
    {
        _markers = markers;
        InvalidateVisual();
    }

    public void ClearPin()
    {
        _pinned = null;
        PointSelected?.Invoke(this, null);
        InvalidateVisual();
    }

    internal static string FormatNumber(double value) => Math.Abs(value) switch
    {
        >= 1000 => value.ToString("#,0", CultureInfo.CurrentCulture),
        >= 100 => value.ToString("0", CultureInfo.CurrentCulture),
        >= 10 => value.ToString("0.#", CultureInfo.CurrentCulture),
        _ => value.ToString("0.##", CultureInfo.CurrentCulture)
    };

    // ---------- layout helpers ----------

    private List<LiveChartSeries> Visible => _series.Where(series => series.IsVisible && series.HasData).ToList();

    private (DateTime Start, DateTime End) TimeWindow()
    {
        DateTime end = _times.Count > 0 ? _times[^1] : DateTime.Now;
        return (end - _range, end);
    }

    private Rect PlotArea => new(AxisWidth, 0, Math.Max(10, ActualWidth - AxisWidth - RightPadding), Math.Max(10, ActualHeight - TimeAxisHeight));

    private double XFor(DateTime time, Rect plot, DateTime start, DateTime end)
    {
        double fraction = (time - start).TotalMilliseconds / Math.Max(1, (end - start).TotalMilliseconds);
        return plot.Left + fraction * plot.Width;
    }

    private Rect LaneRect(int index, int count, Rect plot)
    {
        double laneHeight = (plot.Height - LaneGap * (count - 1)) / count;
        return new Rect(plot.Left, plot.Top + index * (laneHeight + LaneGap), plot.Width, Math.Max(10, laneHeight));
    }

    private static (double Min, double Max, double Step) NiceAxis(LiveChartSeries series, IEnumerable<double> values)
    {
        double min, max;
        if (series.FixedMin.HasValue && series.FixedMax.HasValue)
        {
            min = series.FixedMin.Value;
            max = series.FixedMax.Value;
        }
        else
        {
            List<double> list = values.ToList();
            if (list.Count == 0)
            {
                return (0, 1, 0.25);
            }

            min = list.Min();
            max = list.Max();
            if (max - min < 1e-6)
            {
                double pad = Math.Max(Math.Abs(max) * 0.1, 1);
                min -= pad;
                max += pad;
            }
            else
            {
                double headroom = (max - min) * 0.15;
                min -= headroom;
                max += headroom;
            }

            if (min < 0 && list.Min() >= 0)
            {
                min = 0;
            }
        }

        double step = NiceStep((max - min) / 4);
        return (Math.Floor(min / step) * step, Math.Ceiling(max / step) * step, step);
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0)
        {
            return 1;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double normalized = raw / magnitude;
        double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 2.5 ? 2.5 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    internal static TimeSpan NiceTimeStep(TimeSpan range)
    {
        foreach (int seconds in new[] { 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400 })
        {
            if (range.TotalSeconds / seconds <= 7)
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        return TimeSpan.FromDays(2);
    }

    private int NearestIndex(DateTime time)
    {
        if (_times.Count == 0)
        {
            return -1;
        }

        int best = 0;
        double bestDistance = double.MaxValue;
        for (int index = 0; index < _times.Count; index++)
        {
            double distance = Math.Abs((_times[index] - time).TotalMilliseconds);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = index;
            }
        }

        return best;
    }

    // ---------- rendering ----------

    protected override void OnRender(DrawingContext context)
    {
        WpfBrush text = Resource("TextBrush", Brushes.Black);
        WpfBrush muted = Resource("MutedTextBrush", Brushes.Gray);
        WpfBrush card = Resource("CardBrush", Brushes.White);
        WpfPen grid = new(new SolidColorBrush(WpfColor.FromArgb(40, 128, 128, 128)), 1) { DashStyle = new DashStyle(new double[] { 3, 4 }, 0) };
        WpfPen axis = new(new SolidColorBrush(WpfColor.FromArgb(90, 128, 128, 128)), 1);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hit-testable

        List<LiveChartSeries> visible = Visible;
        if (visible.Count == 0 || _times.Count == 0)
        {
            DrawText(context, _series.Any(series => series.HasData) ? "Select a reading above to plot it." : EmptyText,
                new WpfPoint(ActualWidth / 2, ActualHeight / 2), muted, 13, dpi, TextAlignment.Center, centerVertically: true);
            return;
        }

        Rect plot = PlotArea;
        (DateTime start, DateTime end) = TimeWindow();

        // Shared time axis: vertical gridlines through every lane plus clock-time labels.
        TimeSpan step = NiceTimeStep(_range);
        DateTime tick = new(start.Ticks - start.Ticks % step.Ticks + step.Ticks, start.Kind);
        string timeFormat = step.TotalSeconds < 60 ? "HH:mm:ss"
            : step.TotalDays >= 1 ? "ddd d"
            : _range > TimeSpan.FromDays(1) ? "ddd HH:mm"
            : "HH:mm";
        for (; tick <= end; tick += step)
        {
            double x = XFor(tick, plot, start, end);
            context.DrawLine(grid, new WpfPoint(x, plot.Top), new WpfPoint(x, plot.Bottom));
            DrawText(context, tick.ToString(timeFormat, CultureInfo.CurrentCulture), new WpfPoint(x, plot.Bottom + 6), muted, 10, dpi, TextAlignment.Center);
        }

        context.DrawLine(axis, new WpfPoint(plot.Left, plot.Bottom), new WpfPoint(plot.Right, plot.Bottom));
        DrawText(context, "Time", new WpfPoint(plot.Right, plot.Bottom + 19), muted, 10, dpi, TextAlignment.Right);

        int hoverIndex = _hover is WpfPoint hover && plot.Contains(hover)
            ? NearestIndex(start + TimeSpan.FromMilliseconds((hover.X - plot.Left) / plot.Width * (end - start).TotalMilliseconds))
            : -1;
        int pinnedIndex = _pinned is DateTime pinned && pinned >= start ? NearestIndex(pinned) : -1;

        for (int lane = 0; lane < visible.Count; lane++)
        {
            LiveChartSeries series = visible[lane];
            Rect laneRect = LaneRect(lane, visible.Count, plot);
            Rect valueRect = new(laneRect.Left, laneRect.Top + LaneHeaderHeight, laneRect.Width, Math.Max(10, laneRect.Height - LaneHeaderHeight));

            List<double> windowValues = new();
            for (int index = 0; index < _times.Count; index++)
            {
                if (_times[index] >= start && series.Values.ElementAtOrDefault(index) is double value)
                {
                    windowValues.Add(value);
                }
            }

            // Keep the limit on screen so the gap between reading and limit is visible.
            (double min, double max, double valueStep) = NiceAxis(series,
                series.Threshold is double limit ? windowValues.Append(limit) : windowValues);
            double span = Math.Max(max - min, 1e-9);
            double YFor(double value) => valueRect.Bottom - (value - min) / span * valueRect.Height;

            // Lane header: colour chip, name and latest value.
            SolidColorBrush stroke = new(series.Color);
            context.DrawRoundedRectangle(stroke, null, new Rect(laneRect.Left, laneRect.Top + 4, 10, 10), 3, 3);
            double? latest = series.Values.LastOrDefault(value => value.HasValue);
            DrawText(context, series.Name, new WpfPoint(laneRect.Left + 16, laneRect.Top + 1), text, 12, dpi, TextAlignment.Left, bold: true);
            DrawText(context, $"now {series.Format(latest)}   min {FormatNumber(windowValues.DefaultIfEmpty().Min())}   max {FormatNumber(windowValues.DefaultIfEmpty().Max())}",
                new WpfPoint(laneRect.Right, laneRect.Top + 2), muted, 10.5, dpi, TextAlignment.Right);

            // Y axis: nice ticks with values, unit on the axis, dashed gridlines.
            for (double value = min; value <= max + valueStep / 2; value += valueStep)
            {
                double y = YFor(value);
                context.DrawLine(grid, new WpfPoint(valueRect.Left, y), new WpfPoint(valueRect.Right, y));
                DrawText(context, FormatNumber(value), new WpfPoint(valueRect.Left - 8, y), muted, 10, dpi, TextAlignment.Right, centerVertically: true);
            }

            DrawText(context, series.Unit, new WpfPoint(4, valueRect.Top - 2), stroke, 10, dpi, TextAlignment.Left, bold: true);
            context.DrawLine(axis, new WpfPoint(valueRect.Left, valueRect.Top), new WpfPoint(valueRect.Left, valueRect.Bottom));

            if (series.Threshold is double threshold)
            {
                WpfBrush danger = Resource("DangerBrush", Brushes.Red);
                double y = Math.Clamp(YFor(threshold), valueRect.Top, valueRect.Bottom);
                context.DrawLine(new WpfPen(danger, 1.5) { DashStyle = new DashStyle(new double[] { 6, 4 }, 0) },
                    new WpfPoint(valueRect.Left, y), new WpfPoint(valueRect.Right, y));
                DrawText(context, $"limit {FormatNumber(threshold)} {series.Unit}", new WpfPoint(valueRect.Right - 4, y - 15), danger, 10, dpi, TextAlignment.Right, bold: true);
            }

            // Line + soft area fill, broken where a sensor gave no reading.
            List<List<WpfPoint>> segments = new();
            List<WpfPoint>? current = null;
            for (int index = 0; index < _times.Count; index++)
            {
                if (_times[index] < start || series.Values.ElementAtOrDefault(index) is not double value)
                {
                    current = null;
                    continue;
                }

                if (current is null)
                {
                    current = new List<WpfPoint>();
                    segments.Add(current);
                }

                current.Add(new WpfPoint(XFor(_times[index], plot, start, end), Math.Clamp(YFor(value), valueRect.Top, valueRect.Bottom)));
            }

            LinearGradientBrush fill = new(WpfColor.FromArgb(70, series.Color.R, series.Color.G, series.Color.B),
                WpfColor.FromArgb(4, series.Color.R, series.Color.G, series.Color.B), 90);
            WpfPen linePen = new(stroke, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            foreach (List<WpfPoint> segment in segments)
            {
                if (segment.Count == 1)
                {
                    context.DrawEllipse(stroke, null, segment[0], 2, 2);
                    continue;
                }

                StreamGeometry area = new();
                using (StreamGeometryContext geometry = area.Open())
                {
                    geometry.BeginFigure(new WpfPoint(segment[0].X, valueRect.Bottom), true, true);
                    geometry.PolyLineTo(segment, false, true);
                    geometry.LineTo(new WpfPoint(segment[^1].X, valueRect.Bottom), false, false);
                }

                context.DrawGeometry(fill, null, area);

                StreamGeometry line = new();
                using (StreamGeometryContext geometry = line.Open())
                {
                    geometry.BeginFigure(segment[0], false, false);
                    geometry.PolyLineTo(segment.Skip(1).ToList(), true, true);
                }

                context.DrawGeometry(null, linePen, line);
            }

            if (segments.Count > 0 && segments[^1].Count > 0)
            {
                context.DrawEllipse(card, linePen, segments[^1][^1], 3.5, 3.5); // latest point
            }

            DrawMarker(context, pinnedIndex, series, plot, start, end, valueRect, YFor, stroke, card, pinned: true);
            DrawMarker(context, hoverIndex, series, plot, start, end, valueRect, YFor, stroke, card, pinned: false);
        }

        foreach ((DateTime time, string label) in _markers)
        {
            if (time < start || time > end)
            {
                continue;
            }

            double x = XFor(time, plot, start, end);
            WpfBrush markerBrush = Resource("WarningBrush", Brushes.Orange);
            context.DrawLine(new WpfPen(markerBrush, 1.5), new WpfPoint(x, plot.Top), new WpfPoint(x, plot.Bottom));
            FormattedText markerText = Text(label, Brushes.White, 10, dpi, bold: true);
            Rect tag = new(Math.Min(x + 3, plot.Right - markerText.Width - 10), plot.Bottom - markerText.Height - 6, markerText.Width + 8, markerText.Height + 2);
            context.DrawRoundedRectangle(markerBrush, null, tag, 4, 4);
            context.DrawText(markerText, new WpfPoint(tag.Left + 4, tag.Top + 1));
        }

        DrawCursorLine(context, pinnedIndex, plot, start, end, pinned: true);
        DrawCursorLine(context, hoverIndex, plot, start, end, pinned: false);
        DrawReadout(context, pinnedIndex >= 0 ? pinnedIndex : hoverIndex, visible, plot, start, end, text, muted, card, dpi, pinnedIndex >= 0);
    }

    private void DrawMarker(DrawingContext context, int index, LiveChartSeries series, Rect plot, DateTime start, DateTime end,
        Rect valueRect, Func<double, double> yFor, SolidColorBrush stroke, WpfBrush card, bool pinned)
    {
        if (index < 0 || series.Values.ElementAtOrDefault(index) is not double value)
        {
            return;
        }

        WpfPoint point = new(XFor(_times[index], plot, start, end), Math.Clamp(yFor(value), valueRect.Top, valueRect.Bottom));
        context.DrawEllipse(pinned ? stroke : card, new WpfPen(stroke, 2), point, pinned ? 5 : 4, pinned ? 5 : 4);
    }

    private void DrawCursorLine(DrawingContext context, int index, Rect plot, DateTime start, DateTime end, bool pinned)
    {
        if (index < 0)
        {
            return;
        }

        double x = XFor(_times[index], plot, start, end);
        WpfPen pen = pinned
            ? new WpfPen(Resource("PrimaryBrush", Brushes.DodgerBlue), 1.5)
            : new WpfPen(new SolidColorBrush(WpfColor.FromArgb(120, 128, 128, 128)), 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        context.DrawLine(pen, new WpfPoint(x, plot.Top), new WpfPoint(x, plot.Bottom));
    }

    private void DrawReadout(DrawingContext context, int index, List<LiveChartSeries> visible, Rect plot, DateTime start, DateTime end,
        WpfBrush text, WpfBrush muted, WpfBrush card, double dpi, bool pinned)
    {
        if (index < 0)
        {
            return;
        }

        List<(string Line, WpfBrush Brush)> lines = new()
        {
            ((pinned ? "Pinned  " : string.Empty) + _times[index].ToString(PointTimeFormat, CultureInfo.CurrentCulture), muted)
        };
        lines.AddRange(visible.Select(series => ($"{series.Name}: {series.Format(series.Values.ElementAtOrDefault(index))}", (WpfBrush)new SolidColorBrush(series.Color))));

        List<FormattedText> texts = lines.Select((line, row) => Text(line.Line, row == 0 ? muted : text, row == 0 ? 10.5 : 11.5, dpi, bold: row > 0)).ToList();
        double width = texts.Max(item => item.Width) + 30;
        double height = texts.Sum(item => item.Height + 2) + 14;
        double x = XFor(_times[index], plot, start, end);
        double left = x + 14 + width > plot.Right ? x - 14 - width : x + 14;
        Rect box = new(Math.Max(plot.Left, left), plot.Top + LaneHeaderHeight + 6, width, height);

        context.DrawRoundedRectangle(card, new WpfPen(Resource("BorderBrush", Brushes.LightGray), 1), box, 8, 8);
        double y = box.Top + 7;
        for (int row = 0; row < texts.Count; row++)
        {
            if (row > 0)
            {
                context.DrawEllipse(lines[row].Brush, null, new WpfPoint(box.Left + 12, y + texts[row].Height / 2), 3.5, 3.5);
            }

            context.DrawText(texts[row], new WpfPoint(box.Left + (row == 0 ? 10 : 22), y));
            y += texts[row].Height + 2;
        }
    }

    private WpfBrush Resource(string key, WpfBrush fallback) => TryFindResource(key) as WpfBrush ?? fallback;

    private FormattedText Text(string value, WpfBrush brush, double size, double dpi, bool bold = false) =>
        new(value, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);

    private void DrawText(DrawingContext context, string value, WpfPoint anchor, WpfBrush brush, double size, double dpi,
        TextAlignment alignment, bool centerVertically = false, bool bold = false)
    {
        FormattedText formatted = Text(value, brush, size, dpi, bold);
        double x = alignment switch
        {
            TextAlignment.Right => anchor.X - formatted.Width,
            TextAlignment.Center => anchor.X - formatted.Width / 2,
            _ => anchor.X
        };
        double y = centerVertically ? anchor.Y - formatted.Height / 2 : anchor.Y;
        context.DrawText(formatted, new WpfPoint(x, y));
    }

    // ---------- interaction ----------

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Focus();
        Rect plot = PlotArea;
        WpfPoint position = e.GetPosition(this);
        if (!plot.Contains(position) || _times.Count == 0)
        {
            return;
        }

        (DateTime start, DateTime end) = TimeWindow();
        int index = NearestIndex(start + TimeSpan.FromMilliseconds((position.X - plot.Left) / plot.Width * (end - start).TotalMilliseconds));
        if (index < 0)
        {
            return;
        }

        _pinned = _times[index];
        PointSelected?.Invoke(this, _times[index].ToString(PointTimeFormat, CultureInfo.CurrentCulture) + "   " + string.Join("   ",
            Visible.Select(series => $"{series.Name} {series.Format(series.Values.ElementAtOrDefault(index))}")));
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        ClearPin();
        e.Handled = true;
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            ClearPin();
        }
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 360 : availableSize.Height);
}
