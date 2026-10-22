using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WindowsUtility.Controls;
using WindowsUtility.Dialogs;
using WindowsUtility.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace WindowsUtility;

/// <summary>
/// Subject hubs: one tabbed window per subject (CPU & Cooling, Graphics, Memory, Storage, Logs) so everything related
/// is in one place. Tiles, health checks and the old per-feature buttons open the matching hub and tab.
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, HubWindow> _hubs = new();

    private HubWindow OpenHub(string key, Func<HubWindow> build, string? tab)
    {
        if (!_hubs.TryGetValue(key, out HubWindow? hub))
        {
            hub = build();
            hub.Owner = this;
            hub.Closed += (_, _) => _hubs.Remove(key);
            _hubs[key] = hub;
            hub.Show();
        }
        else
        {
            if (hub.WindowState == WindowState.Minimized)
            {
                hub.WindowState = WindowState.Normal;
            }

            hub.Activate();
        }

        if (tab is not null)
        {
            hub.SelectTab(tab);
        }

        return hub;
    }

    private void OpenCpuHub(string? tab = null) => OpenHub("cpu", () =>
    {
        HubWindow hub = new("cpu", "", "CPU & Cooling",
            "Live processor readings, temperature history, fans, sensors and the thermal load test in one place.");
        hub.AddTab("Live graph", new LiveGraphPanel(GraphComponent.Cpu));
        hub.AddWindowTab("Temperature history", new CpuTemperatureLogWindow(_cpuTemperatureLogService));
        hub.AddTab("Sensors & tools", ActionPanel(
            ("Sensor", CpuSensorHelpText.Text),
            ("Alert", $"Alert above {_appSettings.CpuTemperatureAlertThresholdCelsius:0} °C, sound {(_appSettings.CpuTemperatureSoundAlertEnabled ? "on" : "off")}, repeating every {_appSettings.CpuTemperatureAlertIntervalSeconds / 60} min. Change it on the CPU Sensor & Alerts tile."),
            ("Fans", $"{CpuFanSpeedText.Text}\n{CaseFanSpeedText.Text}"),
            new[]
            {
                ActionButton("Fan diagnostics report", "", "Every fan sensor the hardware exposes, opened in Notepad.", () => FanDiagnosticsButton_Click(this, new RoutedEventArgs())),
                ActionButton("Rescan CPU sensors", "", "Look for the temperature sensor again, e.g. after installing a driver.", () => RescanCpuSensorsButton_Click(this, new RoutedEventArgs())),
                ActionButton("CPU & GPU thermal load test", "", "Supervised heat test with automatic temperature stops.", () => OpenThermalLoadTestButton_Click(this, new RoutedEventArgs()))
            }));
        hub.Closed += async (_, _) => await ReloadCpuTemperatureLogsAsync();
        return hub;
    }, tab);

    private void OpenGpuHub(string? tab = null) => OpenHub("gpu", () =>
    {
        HubWindow hub = new("gpu", "", "Graphics",
            "Live graphics readings, the installed adapters and displays, and the GPU load test.");
        hub.AddTab("Live graph", new LiveGraphPanel(GraphComponent.Gpu));
        hub.AddTab("Adapters & displays", ListPanel(
            ("Graphics adapters", GpuInventorySummaryText.Text, GpuInventoryList.ItemsSource),
            ("Displays", DisplayInventorySummaryText.Text, DisplayInventoryList.ItemsSource)));
        hub.AddTab("Load test", ActionPanel(null, null, null, new[]
        {
            ActionButton("CPU & GPU thermal load test", "", "GPU render or combined load while watching real temperatures.", () => OpenThermalLoadTestButton_Click(this, new RoutedEventArgs()))
        }));
        return hub;
    }, tab);

    private void OpenMemoryHub(string? tab = null) => OpenHub("memory", () =>
    {
        HubWindow hub = new("memory", "", "Memory",
            "Live memory use, the installed modules and high-memory incidents.");
        hub.AddTab("Live graph", new LiveGraphPanel(GraphComponent.Memory));
        hub.AddTab("Installed modules", ListPanel(("Memory modules", $"{RamModuleSummaryText.Text}  •  {RamUsageDetailText.Text}", RamModuleInventoryList.ItemsSource)));
        hub.AddWindowTab("Incidents", new SystemHealthLogWindow(_systemHealthLogService));
        return hub;
    }, tab);

    private void OpenStorageHub(string? tab = null) => OpenHub("storage", () =>
    {
        HubWindow hub = new("storage", "", "Storage & disks",
            "Drive health, speed and capacity tests, guarded disk tools, cleanup and the Windows disk consoles.");
        hub.AddWindowTab("Health & speed", new StorageDiagnosticsWindow());
        if (PrivilegedOperations.IsAdministrator)
        {
            hub.AddWindowTab("Disk tools", new DiskPreparationWindow());
        }
        else
        {
            // Wiping, partitioning and formatting stay behind an administrator password.
            hub.AddTab("Disk tools", ActionPanel(
                ("Administrator", "Disk tools clean, initialize, partition and format disks, so they need an administrator. They open in their own window after Windows asks for an administrator password."),
                null, null, new[]
                {
                    ActionButton("Open disk tools as administrator", "", "List disks and partitions, then clean, initialize, partition or format.", () =>
                        StatusText.Text = ElevatedTool.Open(ElevatedTool.DiskTools)
                            ? "Opened disk tools as administrator."
                            : "Disk tools were not opened (the administrator prompt was cancelled).")
                }));
        }
        hub.AddTab("Clone & migrate", ActionPanel(
            ("Clone a disk", "Copy a whole disk to a new SSD: every partition is copied (Windows from a snapshot while it runs), the new disk is made to start Windows, and a bigger SSD's extra space goes to Windows. The old disk is only read."),
            ("One partition", "Copy a single partition into free space or over another partition, on the same or another disk."),
            ("Administrator", PrivilegedOperations.IsAdministrator ? "Opens in its own window." : "Opens in its own window after Windows asks for an administrator password."),
            new[]
            {
                ActionButton("Open clone & migrate", "", "Plan and run a disk clone or a partition copy.", OpenDiskCloneWindow)
            }));
        hub.AddTab("Cleanup & consoles", ActionPanel(null, null, null, new[]
        {
            ActionButton("Clear temporary files", "", "Clean user and Windows temp folders and show a report.", () => ClearTemporaryFilesButton_Click(this, new RoutedEventArgs())),
            ActionButton("Disk Cleanup", "", "Windows Disk Cleanup for system files.", () => OpenDiskCleanupButton_Click(this, new RoutedEventArgs())),
            ActionButton("Storage Sense", "", "Windows automatic cleanup rules.", () => OpenStorageSenseButton_Click(this, new RoutedEventArgs())),
            ActionButton("Disk Management", "", "Native Windows volumes and partitions console.", () => OpenDiskManagementButton_Click(this, new RoutedEventArgs())),
            ActionButton("DiskPart (Admin)", "", "Elevated command-line disk administration.", () => OpenDiskPartButton_Click(this, new RoutedEventArgs()))
        }));
        return hub;
    }, tab);

    private void OpenLogsHub(string? tab = null) => OpenHub("logs", () =>
    {
        HubWindow hub = new("logs", "", "Logs & history",
            "Every log in one place: CPU temperature, system health events, the background service and the app itself.");
        hub.AddWindowTab("CPU temperature", new CpuTemperatureLogWindow(_cpuTemperatureLogService));
        hub.AddWindowTab("System health events", new SystemHealthLogWindow(_systemHealthLogService));
        hub.AddTab("Service log", LogFilePanel(SharedDataPaths.ServiceLogFilePath));
        hub.AddTab("Application log", LogFilePanel(ApplicationLogService.LogFilePath));
        hub.Closed += async (_, _) => await ReloadCpuTemperatureLogsAsync();
        return hub;
    }, tab);

    private void OpenDiskCloneWindow()
    {
        if (PrivilegedOperations.IsAdministrator)
        {
            new DiskCloneWindow { Owner = this }.Show();
            return;
        }

        StatusText.Text = ElevatedTool.Open(ElevatedTool.DiskClone)
            ? "Opened clone & migrate as administrator."
            : "Clone & migrate was not opened (the administrator prompt was cancelled).";
    }

    private void OpenLogsHubButton_Click(object sender, RoutedEventArgs e) => OpenLogsHub();

    private void OpenStorageHubButton_Click(object sender, RoutedEventArgs e) => OpenStorageHub();

    private void StorageCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenStorageHub();

    private void InstalledMemoryCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenMemoryHub("Installed modules");

    private void GraphicsAdaptersCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenGpuHub("Adapters & displays");

    // ---------- small builders for hub tabs ----------

    private static FrameworkElement Card(UIElement child)
    {
        Border border = new() { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12), Child = child };
        border.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return border;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

    private static TextBlock Muted(string text)
    {
        TextBlock block = new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        return block;
    }

    private static (string Title, string Glyph, string Description, Action Run) ActionButton(string title, string glyph, string description, Action run) =>
        (title, glyph, description, run);

    private static ScrollViewer ActionPanel((string Title, string Text)? first, (string Title, string Text)? second, (string Title, string Text)? third,
        IEnumerable<(string Title, string Glyph, string Description, Action Run)> actions)
    {
        StackPanel stack = new();
        foreach ((string Title, string Text)? info in new[] { first, second, third })
        {
            if (info is { } item && !string.IsNullOrWhiteSpace(item.Text))
            {
                StackPanel content = new();
                content.Children.Add(Heading(item.Title));
                content.Children.Add(Muted(item.Text));
                stack.Children.Add(Card(content));
            }
        }

        UniformGrid grid = new() { Columns = 2 };
        foreach ((string title, string glyph, string description, Action run) in actions)
        {
            StackPanel tile = new() { Margin = new Thickness(0, 0, 12, 12) };
            WpfButton button = new() { Content = title, Tag = glyph, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0) };
            button.SetResourceReference(StyleProperty, "IconCompactPrimaryButton");
            button.Click += (_, _) => run();
            tile.Children.Add(button);
            TextBlock help = Muted(description);
            help.Margin = new Thickness(2, 6, 0, 0);
            tile.Children.Add(help);
            grid.Children.Add(tile);
        }

        if (grid.Children.Count > 0)
        {
            StackPanel actionsContent = new();
            actionsContent.Children.Add(Heading("Actions"));
            actionsContent.Children.Add(grid);
            stack.Children.Add(Card(actionsContent));
        }

        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 12, 0, 0) };
    }

    private static ScrollViewer ListPanel(params (string Title, string Summary, IEnumerable? Items)[] sections)
    {
        StackPanel stack = new();
        foreach ((string title, string summary, IEnumerable? items) in sections)
        {
            StackPanel content = new();
            content.Children.Add(Heading(title));
            content.Children.Add(Muted(summary));
            foreach (object item in items ?? Array.Empty<object>())
            {
                content.Children.Add(new TextBlock { Text = item?.ToString(), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0) });
            }

            stack.Children.Add(Card(content));
        }

        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 12, 0, 0) };
    }

    /// <summary>Last part of a text log, newest at the bottom, with Refresh and Open in Notepad.</summary>
    private static Grid LogFilePanel(string path)
    {
        Grid grid = new() { Margin = new Thickness(0, 12, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        WpfTextBox text = new()
        {
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8)
        };

        void Load()
        {
            try
            {
                if (!File.Exists(path))
                {
                    text.Text = "No entries have been recorded yet.";
                    return;
                }

                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream);
                string[] lines = reader.ReadToEnd().Split('\n');
                text.Text = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 600))).TrimEnd();
                text.ScrollToEnd();
            }
            catch (Exception exception)
            {
                text.Text = $"The log could not be read: {exception.Message}";
            }
        }

        DockPanel bar = new() { Margin = new Thickness(0, 0, 0, 8) };
        StackPanel buttons = new() { Orientation = WpfOrientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        WpfButton refresh = new() { Content = "Refresh", Tag = "", Margin = new Thickness(0, 0, 8, 0) };
        refresh.SetResourceReference(StyleProperty, "IconCompactButton");
        refresh.Click += (_, _) => Load();
        WpfButton notepad = new() { Content = "Open in Notepad", Tag = "", Margin = new Thickness(0) };
        notepad.SetResourceReference(StyleProperty, "IconCompactButton");
        notepad.Click += (_, _) =>
        {
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
            }
        };
        buttons.Children.Add(refresh);
        buttons.Children.Add(notepad);
        bar.Children.Add(buttons);
        bar.Children.Add(Muted($"Newest entries at the bottom  •  {path}"));
        grid.Children.Add(bar);

        Grid.SetRow(text, 1);
        grid.Children.Add(text);
        grid.Loaded += (_, _) => Load();
        return grid;
    }
}
