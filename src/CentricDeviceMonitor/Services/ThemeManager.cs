using Microsoft.Win32;
using System.Windows;
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
        return dark;
    }

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
