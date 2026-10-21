using Color = System.Windows.Media.Color;
using CentricDeviceMonitor.Services;
using Xunit;

namespace CentricDeviceMonitor.Tests;

public class AccentColourTests
{
    private static double ContrastWithWhite(Color color) => 1.05 / (ThemeManager.Luminance(color) + 0.05);

    [Theory]
    [InlineData(0xFF, 0xE0, 0x66)] // light yellow accent
    [InlineData(0x99, 0xEB, 0xFF)] // light cyan accent
    [InlineData(0xFF, 0xFF, 0xFF)] // white
    public void LightAccents_AreDarkenedUntilWhiteTextIsReadable(byte r, byte g, byte b)
    {
        Color readable = ThemeManager.ReadableAccent(Color.FromRgb(r, g, b));
        Assert.True(ContrastWithWhite(readable) >= 4.5, $"contrast {ContrastWithWhite(readable):0.0}");
    }

    [Fact]
    public void TheDefaultWindowsBlue_IsKeptAsItIs()
    {
        Color windowsBlue = Color.FromRgb(0x00, 0x78, 0xD4);
        Assert.Equal(windowsBlue, ThemeManager.ReadableAccent(windowsBlue));
    }

    [Fact]
    public void VeryDarkAccents_AreLightenedSoTheyShowOnDarkPages()
    {
        Color readable = ThemeManager.ReadableAccent(Color.FromRgb(0x10, 0x10, 0x30));
        Assert.True(ThemeManager.Luminance(readable) >= 0.07);
        Assert.True(ContrastWithWhite(readable) >= 4.5);
    }
}
