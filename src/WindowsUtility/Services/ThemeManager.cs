using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WpfApplication = System.Windows.Application;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace WindowsUtility.Services;

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

        WpfColor primary = UseWindowsAccent && TryReadWindowsAccent(out WpfColor accent)
            ? ReadableAccent(accent)
            : (WpfColor)WpfColorConverter.ConvertFromString("#007AFF");
        SetBrush(resources, "PrimaryBrush", primary);
        SetBrush(resources, "PrimaryHoverBrush", dark ? Blend(primary, Colors.White, 0.14) : Blend(primary, Colors.Black, 0.12));
        SetBrush(resources, "PrimaryPressedBrush", Blend(primary, Colors.Black, 0.24));
        SetBrush(resources, "ScrollThumbBrush", dark ? "#55FFFFFF" : "#40000000");
        SetBrush(resources, "ScrollThumbHoverBrush", dark ? "#99FFFFFF" : "#77000000");
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
            SetBrush(resources, "SelectedBrush", WpfColor.FromArgb(0x33, primary.R, primary.G, primary.B));
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
            SetBrush(resources, "SelectedBrush", WpfColor.FromArgb(0x14, primary.R, primary.G, primary.B));
            SetBrush(resources, "BorderBrush", "#0F000000");
            SetBrush(resources, "TextBrush", "#1D1D1F");
            SetBrush(resources, "MutedTextBrush", "#6E6E73");
            SetBrush(resources, "TertiaryTextBrush", "#8E8E93");
            SetBrush(resources, "InputBrush", "#FFFFFF");
            SetBrush(resources, "OverlayBrush", "#66000000");
        }
    }

    /// <summary>Use the Windows accent colour (Settings > Personalization > Colors) instead of the app's blue.</summary>
    public static bool UseWindowsAccent { get; set; } = true;

    /// <summary>The accent colour Windows uses for title bars and Start (DWM, stored as 0xAABBGGRR).</summary>
    private static bool TryReadWindowsAccent(out WpfColor color)
    {
        color = default;
        try
        {
            using RegistryKey? dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM", writable: false);
            if (dwm?.GetValue("AccentColor") is int value)
            {
                uint abgr = unchecked((uint)value);
                color = WpfColor.FromRgb((byte)(abgr & 0xFF), (byte)(abgr >> 8 & 0xFF), (byte)(abgr >> 16 & 0xFF));
                return true;
            }
        }
        catch
        {
            // Registry not readable: keep the app's own blue.
        }

        return false;
    }

    /// <summary>
    /// Keeps white button text readable on the accent (contrast of at least 4.5:1) and the accent visible as text on
    /// dark pages: very light accents are darkened and very dark ones lightened.
    /// </summary>
    internal static WpfColor ReadableAccent(WpfColor accent)
    {
        WpfColor color = accent;
        for (int step = 0; step < 20 && Luminance(color) > 0.1833; step++) // white text contrast >= 4.5:1
        {
            color = Blend(color, Colors.Black, 0.08);
        }

        for (int step = 0; step < 20 && Luminance(color) < 0.07; step++)
        {
            color = Blend(color, Colors.White, 0.08);
        }

        return color;
    }

    internal static double Luminance(WpfColor color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static WpfColor Blend(WpfColor from, WpfColor to, double amount) => WpfColor.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private static void SetBrush(ResourceDictionary resources, string key, WpfColor color)
    {
        if (resources[key] is SolidColorBrush existingBrush && !existingBrush.IsFrozen)
        {
            existingBrush.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
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
