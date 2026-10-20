using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WpfApplication = System.Windows.Application;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace CentricDeviceMonitor.Services;

public static class ThemeManager
{
    public const string SystemPreference = "System";
    public const string LightPreference = "Light";
    public const string DarkPreference = "Dark";

    public static string NormalizePreference(string? preference)
    {
        if (string.Equals(preference, LightPreference, StringComparison.OrdinalIgnoreCase))
        {
            return LightPreference;
        }

        if (string.Equals(preference, DarkPreference, StringComparison.OrdinalIgnoreCase))
        {
            return DarkPreference;
        }

        return SystemPreference;
    }

    public static bool Apply(string? preference)
    {
        string normalized = NormalizePreference(preference);
        bool dark = normalized switch
        {
            DarkPreference => true,
            LightPreference => false,
            _ => IsWindowsAppThemeDark()
        };

        ApplyPalette(dark);
        IsDark = dark;
        foreach (Window window in WpfApplication.Current.Windows)
        {
            ApplyWindowChrome(window);
        }

        return dark;
    }

    /// <summary>True while the dark palette is applied.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>
    /// Matches the window's title bar to the theme: dark title bar text and buttons in dark mode, and on Windows 11
    /// the title bar colour of the page. Without this the title bars stayed white in dark mode.
    /// </summary>
    public static void ApplyWindowChrome(Window window)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int dark = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            {
                // Windows 10 before 20H1 used attribute 19 for the same setting.
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeOld, ref dark, sizeof(int));
            }

            WpfColor page = (WpfColor)WpfColorConverter.ConvertFromString(IsDark ? "#1E1E1E" : "#F5F5F7");
            int captionColor = page.R | (page.G << 8) | (page.B << 16);
            DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, sizeof(int)); // Windows 11 only; ignored elsewhere
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // No DWM (very old Windows): the title bar keeps the system colours.
        }
    }

    private const int DwmwaUseImmersiveDarkModeOld = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static bool IsWindowsAppThemeDark()
    {
        try
        {
            using RegistryKey? personalize = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: false);

            object? rawValue = personalize?.GetValue("AppsUseLightTheme");
            if (rawValue is int appsUseLightTheme)
            {
                return appsUseLightTheme == 0;
            }
        }
        catch
        {
            // If Windows theme settings cannot be read, use the light palette.
        }

        return false;
    }

    private static void ApplyPalette(bool dark)
    {
        ResourceDictionary resources = WpfApplication.Current.Resources;

        SetBrush(resources, "PrimaryBrush", "#007AFF");
        SetBrush(resources, "PrimaryHoverBrush", dark ? "#0A84FF" : "#0067D8");
        SetBrush(resources, "PrimaryPressedBrush", dark ? "#006CD8" : "#0057B8");
        SetBrush(resources, "SuccessBrush", dark ? "#30D158" : "#34C759");
        SetBrush(resources, "WarningBrush", dark ? "#FFD60A" : "#FF9F0A");
        SetBrush(resources, "DangerBrush", dark ? "#FF453A" : "#FF3B30");

        if (dark)
        {
            SetBrush(resources, "PageBrush", "#1E1E1E");
            SetBrush(resources, "CardBrush", "#2C2C2E");
            SetBrush(resources, "SurfaceBrush", "#2C2C2E");
            SetBrush(resources, "ToolbarBrush", "#F22C2C2E");
            SetBrush(resources, "SidebarBrush", "#252527");
            SetBrush(resources, "SecondarySurfaceBrush", "#3A3A3C");
            SetBrush(resources, "HoverBrush", "#14FFFFFF");
            SetBrush(resources, "SelectedBrush", "#33007AFF");
            SetBrush(resources, "BorderBrush", "#14FFFFFF");
            SetBrush(resources, "TextBrush", "#F5F5F7");
            SetBrush(resources, "MutedTextBrush", "#AEAEB2");
            SetBrush(resources, "TertiaryTextBrush", "#8E8E93");
            SetBrush(resources, "InputBrush", "#3A3A3C");
            SetBrush(resources, "OverlayBrush", "#8A000000");
        }
        else
        {
            SetBrush(resources, "PageBrush", "#F5F5F7");
            SetBrush(resources, "CardBrush", "#FFFFFF");
            SetBrush(resources, "SurfaceBrush", "#FFFFFF");
            SetBrush(resources, "ToolbarBrush", "#F2FFFFFF");
            SetBrush(resources, "SidebarBrush", "#EBEBF0");
            SetBrush(resources, "SecondarySurfaceBrush", "#F5F5F7");
            SetBrush(resources, "HoverBrush", "#0D000000");
            SetBrush(resources, "SelectedBrush", "#14007AFF");
            SetBrush(resources, "BorderBrush", "#0F000000");
            SetBrush(resources, "TextBrush", "#1D1D1F");
            SetBrush(resources, "MutedTextBrush", "#6E6E73");
            SetBrush(resources, "TertiaryTextBrush", "#8E8E93");
            SetBrush(resources, "InputBrush", "#FFFFFF");
            SetBrush(resources, "OverlayBrush", "#66000000");
        }
    }

    private static void SetBrush(ResourceDictionary resources, string key, string colorValue)
    {
        WpfColor color = (WpfColor)WpfColorConverter.ConvertFromString(colorValue);

        if (resources[key] is SolidColorBrush existingBrush && !existingBrush.IsFrozen)
        {
            existingBrush.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }
}
