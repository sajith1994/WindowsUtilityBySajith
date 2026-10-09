using System.Windows;
using System.Windows.Controls;
using CentricDeviceMonitor.Dialogs;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// Keeps every window inside the Windows work area. Several dialogs were designed for 768px or taller
/// screens; on a 1024x600 or 1366x600 display their minimum size is larger than the screen. When that
/// happens the window is shrunk to the work area and its content is put in a scroll viewer that keeps
/// the original minimum size, so every control stays reachable instead of being cut off.
/// </summary>
public static class WindowFitter
{
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || window is DesktopWidgetWindow || !ReferenceEquals(e.OriginalSource, window))
        {
            return;
        }

        try
        {
            Fit(window);
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Fit window to screen", exception);
        }
    }

    public static void Fit(Window window)
    {
        Rect workArea = SystemParameters.WorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0 || window.WindowState == WindowState.Maximized)
        {
            return;
        }

        double chromeWidth = window.Content is FrameworkElement measured && measured.ActualWidth > 0
            ? Math.Max(0, window.ActualWidth - measured.ActualWidth)
            : 16;
        double chromeHeight = window.Content is FrameworkElement measuredHeight && measuredHeight.ActualHeight > 0
            ? Math.Max(0, window.ActualHeight - measuredHeight.ActualHeight)
            : 39;

        bool tooTall = window.MinHeight > workArea.Height || window.ActualHeight > workArea.Height;
        bool tooWide = window.MinWidth > workArea.Width || window.ActualWidth > workArea.Width;
        if (!tooTall && !tooWide)
        {
            return;
        }

        // Keep the designed minimum size on the content and let it scroll inside the smaller window.
        if (window.Content is FrameworkElement content && content is not ScrollViewer
            && (window.MinHeight > workArea.Height || window.MinWidth > workArea.Width))
        {
            content.MinHeight = Math.Max(content.MinHeight, window.MinHeight - chromeHeight);
            content.MinWidth = Math.Max(content.MinWidth, window.MinWidth - chromeWidth);
            window.Content = null;
            window.Content = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = false
            };
        }

        window.SizeToContent = SizeToContent.Manual;
        window.MinHeight = Math.Min(window.MinHeight, workArea.Height);
        window.MinWidth = Math.Min(window.MinWidth, workArea.Width);
        if (tooTall)
        {
            window.Height = workArea.Height;
        }

        if (tooWide)
        {
            window.Width = workArea.Width;
        }

        window.Left = Math.Clamp(window.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - window.Width));
        window.Top = Math.Clamp(window.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - window.Height));
    }
}
