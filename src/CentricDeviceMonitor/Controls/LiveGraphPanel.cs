using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CentricDeviceMonitor.Services;
using WpfColor = System.Windows.Media.Color;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace CentricDeviceMonitor.Controls;

public enum GraphComponent
{
    Cpu,
    Gpu,
    Memory
}

/// <summary>
/// Live graph for one component: range/sample-rate pickers, a toggle chip per reading, the <see cref="LiveChart"/>
/// and the pinned point's values. Samples keep running while the hosting window is open (also when its tab is not
/// selected); call <see cref="Dispose"/> when the window closes.
/// </summary>
public sealed class LiveGraphPanel : Grid, IDisposable
{
    private const int MaxSamples = 600 * 4;

    private readonly GraphComponent _component;
    private readonly HardwareMonitorService _hardware = new();
    private readonly SystemResourceMonitorService _resources = new();
    private readonly List<DateTime> _times = new();
    private readonly List<LiveChartSeries> _series = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly LiveChart _chart = new();
    private readonly TextBlock _pinnedText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, FontWeight = FontWeights.SemiBold };
    private readonly WrapPanel _chips = new() { VerticalAlignment = VerticalAlignment.Center };
    private TimeSpan _range = TimeSpan.FromMinutes(1);
    private bool _sampling;
    private bool _started;
    private bool _disposed;

    public LiveGraphPanel(GraphComponent component)
    {
        _component = component;
        BuildSeries();
        BuildLayout();
        _timer.Tick += async (_, _) => await SampleAsync();
        _chart.PointSelected += (_, text) => _pinnedText.Text = text is null
            ? "Click any point on the graph to pin it and see every reading at that moment. Right-click or Esc clears."
            : $"Pinned  {text}";
        Loaded += async (_, _) =>
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            await SampleAsync();
            _timer.Start();
        };
    }

    private void BuildSeries()
    {
        switch (_component)
        {
            case GraphComponent.Cpu:
                _series.Add(new LiveChartSeries("Load", "%", Rgb(0x25, 0x63, 0xEB), 0, 100));
                _series.Add(new LiveChartSeries("Temperature", "°C", Rgb(0xDC, 0x26, 0x26)));
                _series.Add(new LiveChartSeries("Clock", "GHz", Rgb(0x7C, 0x3A, 0xED)));
                _series.Add(new LiveChartSeries("Package power", "W", Rgb(0xD9, 0x77, 0x06)));
                _series.Add(new LiveChartSeries("Fan", "RPM", Rgb(0x08, 0x91, 0xB2)));
                break;
            case GraphComponent.Gpu:
                _series.Add(new LiveChartSeries("Utilisation", "%", Rgb(0x25, 0x63, 0xEB), 0, 100));
                _series.Add(new LiveChartSeries("Temperature", "°C", Rgb(0xDC, 0x26, 0x26)));
                _series.Add(new LiveChartSeries("Power", "W", Rgb(0xD9, 0x77, 0x06)));
                _series.Add(new LiveChartSeries("VRAM used", "GB", Rgb(0x16, 0x93, 0x4A)));
                _series.Add(new LiveChartSeries("Fan", "RPM", Rgb(0x08, 0x91, 0xB2)));
                break;
            default:
                _series.Add(new LiveChartSeries("Memory used", "%", Rgb(0x25, 0x63, 0xEB), 0, 100));
                break;
        }
    }

    private static WpfColor Rgb(byte r, byte g, byte b) => WpfColor.FromRgb(r, g, b);

    private void BuildLayout()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Toolbar: reading chips on the left, range / sample rate / clear on the right.
        Grid toolbar = new() { Margin = new Thickness(0, 0, 0, 10) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (LiveChartSeries series in _series)
        {
            _chips.Children.Add(CreateChip(series));
        }

        toolbar.Children.Add(_chips);
        StackPanel controls = new() { Orientation = WpfOrientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(Label("Show"));
        controls.Children.Add(CreateChoiceGroup(new[] { ("30 s", 30), ("1 min", 60), ("2 min", 120), ("5 min", 300), ("10 min", 600) }, 1,
            seconds => { _range = TimeSpan.FromSeconds(seconds); Redraw(); }));
        controls.Children.Add(Label("every"));
        controls.Children.Add(CreateChoiceGroup(new[] { ("0.25 s", 250), ("0.5 s", 500), ("1 s", 1000), ("2 s", 2000) }, 2,
            milliseconds => _timer.Interval = TimeSpan.FromMilliseconds(milliseconds)));
        System.Windows.Controls.Button clear = new() { Content = "Clear", Margin = new Thickness(8, 0, 0, 0), Tag = "" };
        clear.SetResourceReference(StyleProperty, "IconCompactButton");
        clear.Click += (_, _) =>
        {
            _times.Clear();
            foreach (LiveChartSeries series in _series)
            {
                series.Values.Clear();
            }

            _chart.ClearPin();
            Redraw();
        };
        controls.Children.Add(clear);
        SetColumn(controls, 1);
        toolbar.Children.Add(controls);
        Children.Add(toolbar);

        Border chartCard = new() { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(10, 12, 10, 4), Child = _chart };
        chartCard.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        chartCard.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        SetRow(chartCard, 1);
        Children.Add(chartCard);

        Border pinned = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 10, 0, 0), Child = _pinnedText };
        pinned.SetResourceReference(Border.BackgroundProperty, "SecondarySurfaceBrush");
        _pinnedText.Text = "Click any point on the graph to pin it and see every reading at that moment. Right-click or Esc clears.";
        SetRow(pinned, 2);
        Children.Add(pinned);
    }

    private ToggleButton CreateChip(LiveChartSeries series)
    {
        StackPanel content = new() { Orientation = WpfOrientation.Horizontal };
        content.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(series.Color), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = series.Name, VerticalAlignment = VerticalAlignment.Center });

        ToggleButton chip = new()
        {
            Content = content,
            IsChecked = true,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(10, 4, 10, 4),
            ToolTip = $"Show or hide {series.Name}"
        };
        chip.Template = ChipTemplate;
        chip.Cursor = System.Windows.Input.Cursors.Hand;
        chip.Checked += (_, _) => { series.IsVisible = true; Redraw(); };
        chip.Unchecked += (_, _) => { series.IsVisible = false; Redraw(); };
        return chip;
    }

    /// <summary>Pill-shaped toggle: filled when the reading is plotted, outlined when hidden.</summary>
    internal static readonly ControlTemplate ChipTemplate = CreateChipTemplate();

    /// <summary>A reading toggle chip (colour dot + name) for any LiveChart host.</summary>
    internal static ToggleButton CreateSeriesChip(LiveChartSeries series, Action changed)
    {
        StackPanel content = new() { Orientation = WpfOrientation.Horizontal };
        content.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(series.Color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = series.Name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        ToggleButton chip = new()
        {
            Content = content,
            IsChecked = series.IsVisible,
            Margin = new Thickness(0, 0, 5, 4),
            Padding = new Thickness(8, 3, 8, 3),
            Template = ChipTemplate,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = $"Show or hide {series.Name}"
        };
        chip.Checked += (_, _) => { series.IsVisible = true; changed(); };
        chip.Unchecked += (_, _) => { series.IsVisible = false; changed(); };
        return chip;
    }

    private static ControlTemplate CreateChipTemplate()
    {
        FrameworkElementFactory border = new(typeof(Border)) { Name = "Chip" };
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        border.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        FrameworkElementFactory presenter = new(typeof(ContentPresenter));
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextBrush");
        border.AppendChild(presenter);

        ControlTemplate template = new(typeof(ToggleButton)) { VisualTree = border };
        Trigger isChecked = new() { Property = ToggleButton.IsCheckedProperty, Value = true };
        isChecked.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SelectedBrush"), "Chip"));
        isChecked.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("PrimaryBrush"), "Chip"));
        Trigger isUnchecked = new() { Property = ToggleButton.IsCheckedProperty, Value = false };
        isUnchecked.Setters.Add(new Setter(OpacityProperty, 0.55));
        template.Triggers.Add(isChecked);
        template.Triggers.Add(isUnchecked);
        return template;
    }

    private static TextBlock Label(string text)
    {
        TextBlock label = new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0), FontSize = 11 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return label;
    }

    /// <summary>Single-choice chip row (e.g. 30 s / 1 min / 2 min), styled like the reading chips.</summary>
    private StackPanel CreateChoiceGroup((string Text, int Value)[] options, int selected, Action<int> changed)
    {
        string group = Guid.NewGuid().ToString("N");
        StackPanel row = new() { Orientation = WpfOrientation.Horizontal };
        for (int index = 0; index < options.Length; index++)
        {
            (string text, int value) = options[index];
            System.Windows.Controls.RadioButton choice = new()
            {
                Content = text,
                GroupName = group,
                IsChecked = index == selected,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(9, 3, 9, 3),
                FontSize = 11,
                Template = ChipTemplate,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            choice.Checked += (_, _) => changed(value);
            row.Children.Add(choice);
        }

        return row;
    }

    private void Redraw() => _chart.SetData(_times, _series, _range);

    private async Task SampleAsync()
    {
        if (_sampling || _disposed)
        {
            return;
        }

        _sampling = true;
        try
        {
            double?[] values = await Task.Run(ReadValues);
            if (_disposed)
            {
                return;
            }

            _times.Add(DateTime.Now);
            for (int index = 0; index < _series.Count; index++)
            {
                _series[index].Values.Add(index < values.Length ? values[index] : null);
            }

            if (_times.Count > MaxSamples)
            {
                _times.RemoveAt(0);
                foreach (LiveChartSeries series in _series)
                {
                    series.Values.RemoveAt(0);
                }
            }

            Redraw();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Sample live graph", exception);
        }
        finally
        {
            _sampling = false;
        }
    }

    private double?[] ReadValues()
    {
        switch (_component)
        {
            case GraphComponent.Cpu:
            {
                CpuTemperatureReading temperature = _hardware.GetCpuTemperatureReading();
                CpuClockReading clock = _hardware.GetCpuClockReading();
                CpuPackagePowerReading power = _hardware.GetCpuPackagePowerReading();
                FanInventoryReading fans = _hardware.GetFanInventoryReading();
                SystemResourceReading resources = _resources.Read();
                return new double?[]
                {
                    resources.CpuUsagePercent,
                    temperature.IsAvailable ? temperature.ValueCelsius : null,
                    clock.IsAvailable && clock.Megahertz.HasValue ? clock.Megahertz.Value / 1000.0 : null,
                    power.IsAvailable ? power.Watts : null,
                    FirstFanRpm(fans, FanGroup.Cpu)
                };
            }

            case GraphComponent.Gpu:
            {
                GpuTemperatureReading temperature = _hardware.GetGpuTemperatureReading();
                GpuUtilisationReading load = _hardware.GetGpuUtilisationReading();
                GpuPowerReading power = _hardware.GetGpuPowerReading();
                GpuMemoryReading memory = _hardware.GetGpuMemoryReading();
                FanInventoryReading fans = _hardware.GetFanInventoryReading();
                return new double?[]
                {
                    load.IsAvailable ? load.LoadPercent : null,
                    temperature.IsAvailable ? temperature.ValueCelsius : null,
                    power.IsAvailable ? power.Watts : null,
                    memory.UsedMegabytes.HasValue ? memory.UsedMegabytes.Value / 1024.0 : null,
                    FirstFanRpm(fans, FanGroup.Gpu)
                };
            }

            default:
                return new double?[] { _resources.Read().RamUsagePercent };
        }
    }

    private static double? FirstFanRpm(FanInventoryReading fans, FanGroup group) =>
        fans.IsAvailable ? fans.Fans.FirstOrDefault(fan => fan.Group == group)?.Rpm : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _hardware.Dispose();
    }
}
