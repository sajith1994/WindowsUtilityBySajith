using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CentricDeviceMonitor.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfPoint = System.Windows.Point;

namespace CentricDeviceMonitor.Dialogs;

public enum GraphComponent
{
    Cpu,
    Gpu,
    Memory
}

public partial class ComponentGraphWindow : Window
{
    /// <summary>Longest range offered, at the fastest sample rate. Everything else is a subset.</summary>
    private const int MaxSamples = 600 * 4;

    private const double PlotLeftMargin = 62;
    private const double PlotRightMargin = 62;
    private const double PlotTopMargin = 10;
    private const double PlotBottomMargin = 24;

    private readonly GraphComponent _component;
    private readonly HardwareMonitorService _hardwareMonitorService = new();
    private readonly SystemResourceMonitorService _systemResourceMonitorService = new();
    private readonly List<GraphSeries> _series = new();
    private readonly DispatcherTimer _timer = new();
    private int _rangeSeconds = 60;
    private int _intervalMilliseconds = 1000;
    private bool _sampling;
    private bool _initialized;

    public ComponentGraphWindow(GraphComponent component)
    {
        InitializeComponent();
        _component = component;

        HeadingText.Text = component switch
        {
            GraphComponent.Cpu => "Processor - live readings",
            GraphComponent.Gpu => "Graphics - live readings",
            _ => "Memory - live readings"
        };

        BuildSeries();
        LegendList.ItemsSource = _series;

        _timer.Interval = TimeSpan.FromMilliseconds(_intervalMilliseconds);
        _timer.Tick += async (_, _) => await SampleAsync();

        Loaded += async (_, _) =>
        {
            _initialized = true;
            UpdateScaleNote();
            await SampleAsync();
            _timer.Start();
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _hardwareMonitorService.Dispose();
        };

        SizeChanged += (_, _) => Render();
    }

    private void BuildSeries()
    {
        switch (_component)
        {
            case GraphComponent.Cpu:
                _series.Add(new GraphSeries("Load", "%", Rgb(0x25, 0x63, 0xEB), 0, 100));
                _series.Add(new GraphSeries("Temperature", "°C", Rgb(0xDC, 0x26, 0x26)));
                _series.Add(new GraphSeries("Clock", "GHz", Rgb(0x7C, 0x3A, 0xED)));
                _series.Add(new GraphSeries("Package power", "W", Rgb(0xD9, 0x77, 0x06)));
                _series.Add(new GraphSeries("Fan", "RPM", Rgb(0x08, 0x91, 0xB2)));
                break;

            case GraphComponent.Gpu:
                _series.Add(new GraphSeries("Utilisation", "%", Rgb(0x25, 0x63, 0xEB), 0, 100));
                _series.Add(new GraphSeries("Temperature", "°C", Rgb(0xDC, 0x26, 0x26)));
                _series.Add(new GraphSeries("Power", "W", Rgb(0xD9, 0x77, 0x06)));
                _series.Add(new GraphSeries("VRAM used", "GB", Rgb(0x16, 0x93, 0x4A)));
                _series.Add(new GraphSeries("Fan", "RPM", Rgb(0x08, 0x91, 0xB2)));
                break;

            default:
                _series.Add(new GraphSeries("Memory used", "%", Rgb(0x25, 0x63, 0xEB), 0, 100));
                break;
        }
    }

    private static SolidColorBrush Rgb(byte r, byte g, byte b)
    {
        SolidColorBrush brush = new(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private int SampleCapacity => Math.Max(2, (int)Math.Ceiling(_rangeSeconds * 1000.0 / _intervalMilliseconds));

    private async Task SampleAsync()
    {
        if (_sampling)
        {
            return;
        }

        _sampling = true;
        try
        {
            switch (_component)
            {
                case GraphComponent.Cpu:
                {
                    CpuTemperatureReading temperature = await Task.Run(_hardwareMonitorService.GetCpuTemperatureReading);
                    CpuClockReading clock = await Task.Run(_hardwareMonitorService.GetCpuClockReading);
                    CpuPackagePowerReading power = await Task.Run(_hardwareMonitorService.GetCpuPackagePowerReading);
                    FanInventoryReading fans = await Task.Run(_hardwareMonitorService.GetFanInventoryReading);
                    SystemResourceReading resources = await Task.Run(_systemResourceMonitorService.Read);

                    Append(0, resources.CpuUsagePercent);
                    Append(1, temperature.IsAvailable ? temperature.ValueCelsius : null);
                    Append(2, clock.IsAvailable && clock.Megahertz.HasValue ? clock.Megahertz.Value / 1000.0 : null);
                    Append(3, power.IsAvailable ? power.Watts : null);
                    Append(4, FirstFanRpm(fans, FanGroup.Cpu));
                    break;
                }

                case GraphComponent.Gpu:
                {
                    GpuTemperatureReading temperature = await Task.Run(_hardwareMonitorService.GetGpuTemperatureReading);
                    GpuUtilisationReading load = await Task.Run(_hardwareMonitorService.GetGpuUtilisationReading);
                    GpuPowerReading power = await Task.Run(_hardwareMonitorService.GetGpuPowerReading);
                    GpuMemoryReading memory = await Task.Run(_hardwareMonitorService.GetGpuMemoryReading);
                    FanInventoryReading fans = await Task.Run(_hardwareMonitorService.GetFanInventoryReading);

                    Append(0, load.IsAvailable ? load.LoadPercent : null);
                    Append(1, temperature.IsAvailable ? temperature.ValueCelsius : null);
                    Append(2, power.IsAvailable ? power.Watts : null);
                    Append(3, memory.UsedMegabytes.HasValue ? memory.UsedMegabytes.Value / 1024.0 : null);
                    Append(4, FirstFanRpm(fans, FanGroup.Gpu));
                    break;
                }

                default:
                {
                    SystemResourceReading resources = await Task.Run(_systemResourceMonitorService.Read);
                    Append(0, resources.RamUsagePercent);
                    break;
                }
            }

            Render();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Sample component graph", exception);
        }
        finally
        {
            _sampling = false;
        }
    }

    private static double? FirstFanRpm(FanInventoryReading fans, FanGroup group)
    {
        if (!fans.IsAvailable)
        {
            return null;
        }

        foreach (FanSensorReading fan in fans.Fans)
        {
            if (fan.Group == group)
            {
                return fan.Rpm;
            }
        }

        return null;
    }

    private void Append(int index, double? value)
    {
        if (index >= _series.Count)
        {
            return;
        }

        GraphSeries series = _series[index];
        series.Values.Add(value);
        while (series.Values.Count > MaxSamples)
        {
            series.Values.RemoveAt(0);
        }

        series.Refresh();
    }

    private void Render()
    {
        // Can be reached from a SelectionChanged raised during InitializeComponent, before the
        // named elements below the combo boxes have been created.
        if (!_initialized || GraphCanvas is null)
        {
            return;
        }

        GraphCanvas.Children.Clear();

        double width = GraphCanvas.ActualWidth;
        double height = GraphCanvas.ActualHeight;
        if (width <= 40 || height <= 40)
        {
            return;
        }

        List<GraphSeries> visible = _series.Where(series => series.IsVisible && series.HasData).ToList();
        GraphEmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GraphEmptyText.Text = _series.Any(series => series.HasData)
            ? "No readings selected. Tick one in the legend below."
            : "Collecting samples...";

        if (visible.Count == 0)
        {
            return;
        }

        double plotLeft = PlotLeftMargin;
        double plotRight = width - (visible.Count == 2 ? PlotRightMargin : 12);
        double plotTop = PlotTopMargin;
        double plotBottom = height - PlotBottomMargin;
        double plotWidth = Math.Max(10, plotRight - plotLeft);
        double plotHeight = Math.Max(10, plotBottom - plotTop);

        // One series gets its own labelled axis; two get a left and a right axis; three or more
        // cannot be labelled meaningfully, so they fall back to per-series normalisation.
        bool labelledAxes = visible.Count <= 2;

        DrawFrame(plotLeft, plotTop, plotWidth, plotHeight);
        DrawTimeAxis(plotLeft, plotBottom, plotWidth);

        for (int index = 0; index < visible.Count; index++)
        {
            GraphSeries series = visible[index];
            (double min, double max) = series.AxisRange(SampleCapacity);

            if (labelledAxes)
            {
                DrawValueAxis(series, min, max, index == 0 ? plotLeft : plotLeft + plotWidth, plotTop, plotHeight, index == 0);
            }

            DrawSeries(series, min, max, plotLeft, plotTop, plotWidth, plotHeight);
        }

        UpdateScaleNote(visible.Count);
    }

    private void DrawFrame(double left, double top, double width, double height)
    {
        WpfBrush grid = new SolidColorBrush(System.Windows.Media.Color.FromArgb(38, 128, 128, 128));
        for (int division = 0; division <= 4; division++)
        {
            double y = top + (height * division / 4.0);
            GraphCanvas.Children.Add(new Line
            {
                X1 = left, X2 = left + width, Y1 = y, Y2 = y, Stroke = grid, StrokeThickness = 1
            });
        }
    }

    private void DrawTimeAxis(double left, double bottom, double width)
    {
        for (int division = 0; division <= 4; division++)
        {
            double x = left + (width * division / 4.0);
            // Time runs right to left: the newest sample is at the right edge.
            int secondsAgo = (int)Math.Round(_rangeSeconds * (1 - (division / 4.0)));
            TextBlock label = new()
            {
                Text = secondsAgo == 0 ? "now" : $"-{FormatSeconds(secondsAgo)}",
                FontSize = 10,
                Foreground = (WpfBrush)FindResource("MutedTextBrush")
            };

            label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, x - (label.DesiredSize.Width / 2));
            Canvas.SetTop(label, bottom + 5);
            GraphCanvas.Children.Add(label);
        }
    }

    private static string FormatSeconds(int seconds) =>
        seconds >= 60 && seconds % 60 == 0 ? $"{seconds / 60}m" : $"{seconds}s";

    private void DrawValueAxis(GraphSeries series, double min, double max, double x, double top, double height, bool leftSide)
    {
        for (int division = 0; division <= 4; division++)
        {
            double fraction = 1 - (division / 4.0);
            double value = min + ((max - min) * fraction);
            double y = top + (height * division / 4.0);

            TextBlock label = new()
            {
                Text = division == 0
                    ? $"{FormatValue(value)} {series.Unit}"
                    : FormatValue(value),
                FontSize = 10,
                Foreground = series.Stroke
            };

            label.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, leftSide ? Math.Max(0, x - label.DesiredSize.Width - 6) : x + 6);
            Canvas.SetTop(label, y - (label.DesiredSize.Height / 2));
            GraphCanvas.Children.Add(label);
        }
    }

    private static string FormatValue(double value)
    {
        if (Math.Abs(value) >= 1000)
        {
            return value.ToString("0", CultureInfo.CurrentCulture);
        }

        return Math.Abs(value) >= 10
            ? value.ToString("0.#", CultureInfo.CurrentCulture)
            : value.ToString("0.##", CultureInfo.CurrentCulture);
    }

    private void DrawSeries(GraphSeries series, double min, double max, double left, double top, double width, double height)
    {
        int capacity = SampleCapacity;
        List<double?> window = series.Window(capacity);
        double span = Math.Max(max - min, 0.0001);

        Polyline line = new()
        {
            Stroke = series.Stroke,
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round
        };

        double step = width / Math.Max(capacity - 1, 1);
        int offset = capacity - window.Count;

        // Index by position in the window, not by how many points have been added - a gap where a
        // sensor returned nothing must leave a hole in the line, not shift everything left.
        for (int index = 0; index < window.Count; index++)
        {
            double? value = window[index];
            if (!value.HasValue)
            {
                continue;
            }

            double x = left + ((offset + index) * step);
            double y = top + height - ((value.Value - min) / span * height);
            line.Points.Add(new WpfPoint(x, Math.Clamp(y, top, top + height)));
        }

        if (line.Points.Count > 1)
        {
            GraphCanvas.Children.Add(line);
        }
    }

    private void UpdateScaleNote(int? visibleCount = null)
    {
        if (!_initialized)
        {
            return;
        }

        int count = visibleCount ?? _series.Count(series => series.IsVisible && series.HasData);
        ScaleNoteText.Text = count switch
        {
            0 => "Tick a reading in the legend to plot it.",
            1 => "Single reading: the axis is labelled in its own units.",
            2 => "Two readings: left axis is the first, right axis is the second.",
            _ => "Three or more readings share one chart, so each is scaled to its own range. Untick some for labelled axes."
        };
    }

    private void RangeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _rangeSeconds = ReadTag(sender as WpfComboBox, 60);
        Render();
    }

    private void IntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _intervalMilliseconds = ReadTag(sender as WpfComboBox, 1000);
        _timer.Interval = TimeSpan.FromMilliseconds(_intervalMilliseconds);
        Render();
    }

    private static int ReadTag(WpfComboBox? box, int fallback) =>
        box?.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int value) ? value : fallback;

    private void SeriesVisibility_Changed(object sender, RoutedEventArgs e) => Render();

    private void ShowAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (GraphSeries series in _series)
        {
            series.IsVisible = true;
        }

        Render();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (GraphSeries series in _series)
        {
            series.Values.Clear();
            series.Refresh();
        }

        Render();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>One plotted metric, its visibility, and its live legend text.</summary>
    private sealed class GraphSeries : INotifyPropertyChanged
    {
        private bool _isVisible = true;

        public GraphSeries(string name, string unit, WpfBrush stroke, double? fixedMin = null, double? fixedMax = null)
        {
            Name = name;
            Unit = unit;
            Stroke = stroke;
            FixedMin = fixedMin;
            FixedMax = fixedMax;
        }

        public string Name { get; }

        public string Unit { get; }

        public WpfBrush Stroke { get; }

        /// <summary>Percentages are always plotted 0-100 so the shape is not exaggerated.</summary>
        public double? FixedMin { get; }

        public double? FixedMax { get; }

        public List<double?> Values { get; } = new();

        public bool HasData => Values.Any(value => value.HasValue);

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value)
                {
                    return;
                }

                _isVisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
            }
        }

        public string CurrentDisplay
        {
            get
            {
                double? latest = Values.LastOrDefault(value => value.HasValue);
                return latest.HasValue ? $"{latest.Value:0.##} {Unit}" : "not available";
            }
        }

        public List<double?> Window(int capacity)
        {
            int skip = Math.Max(0, Values.Count - capacity);
            return Values.Skip(skip).ToList();
        }

        /// <summary>
        /// Range for the visible window, padded so a flat line does not sit on the frame.
        /// </summary>
        public (double Min, double Max) AxisRange(int capacity)
        {
            if (FixedMin.HasValue && FixedMax.HasValue)
            {
                return (FixedMin.Value, FixedMax.Value);
            }

            List<double> values = Window(capacity).Where(value => value.HasValue).Select(value => value!.Value).ToList();
            if (values.Count == 0)
            {
                return (0, 1);
            }

            double min = values.Min();
            double max = values.Max();

            if (Math.Abs(max - min) < 0.0001)
            {
                double pad = Math.Max(Math.Abs(max) * 0.1, 1);
                return (min - pad, max + pad);
            }

            double headroom = (max - min) * 0.1;
            return (Math.Max(0, min - headroom), max + headroom);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentDisplay)));
    }
}
