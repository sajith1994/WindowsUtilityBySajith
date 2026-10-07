using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// Shields WPF's legacy input-language bridge from Windows custom/transient locale IDs.
/// Windows can legitimately expose LCID/LANGID 0x1000 (LOCALE_CUSTOM_UNSPECIFIED), while
/// WPF's built-in InputLanguageSource still constructs CultureInfo from the numeric LANGID.
/// </summary>
public sealed class SafeWpfInputLanguageSource : IInputLanguageSource
{
    private const ushort LocaleCustomUnspecified = 0x1000;
    private static readonly CultureInfo UltimateFallbackCulture = CultureInfo.GetCultureInfo("en-US");

    private CultureInfo _fallbackCulture = GetInitialSafeCulture();

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    public CultureInfo CurrentInputLanguage
    {
        get => GetSafeCurrentInputCulture();
        set => _fallbackCulture = Normalize(value);
    }

    public IEnumerable InputLanguageList => new[] { CurrentInputLanguage };

    public void Initialize()
    {
        // No native hook is required. Windows continues to own the actual keyboard layout;
        // this source exists only to give WPF a CultureInfo it can safely consume.
    }

    public void Uninitialize()
    {
        // Nothing to release.
    }

    private CultureInfo GetSafeCurrentInputCulture()
    {
        IntPtr keyboardLayout = GetKeyboardLayout(0);
        if (keyboardLayout != IntPtr.Zero)
        {
            ushort languageId = unchecked((ushort)(keyboardLayout.ToInt64() & 0xFFFF));
            if (languageId != 0 && languageId != LocaleCustomUnspecified)
            {
                try
                {
                    return CultureInfo.GetCultureInfo(languageId);
                }
                catch (CultureNotFoundException)
                {
                    // Transient/custom keyboard IDs can still be nonzero and unmappable.
                }
            }
        }

        return _fallbackCulture;
    }

    private static CultureInfo GetInitialSafeCulture()
    {
        CultureInfo current = CultureInfo.CurrentUICulture;
        if (current.Equals(CultureInfo.InvariantCulture))
        {
            current = CultureInfo.CurrentCulture;
        }

        return Normalize(current);
    }

    private static CultureInfo Normalize(CultureInfo? culture)
    {
        if (culture is null || culture.Equals(CultureInfo.InvariantCulture))
        {
            return UltimateFallbackCulture;
        }

        try
        {
            // Cultures such as custom English/region combinations can be perfectly valid by
            // BCP-47 name while carrying the non-unique Windows LCID 0x1000. Never feed that
            // numeric identifier back into WPF's legacy input-language implementation.
            if (culture.LCID != LocaleCustomUnspecified)
            {
                return CultureInfo.GetCultureInfo(culture.Name);
            }

            string language = culture.TwoLetterISOLanguageName;
            if (!string.IsNullOrWhiteSpace(language) &&
                !string.Equals(language, "iv", StringComparison.OrdinalIgnoreCase))
            {
                CultureInfo specific = CultureInfo.CreateSpecificCulture(language);
                if (specific.LCID != LocaleCustomUnspecified)
                {
                    return CultureInfo.GetCultureInfo(specific.Name);
                }
            }
        }
        catch (CultureNotFoundException)
        {
            // Fall through to the fixed compatibility culture below.
        }

        return UltimateFallbackCulture;
    }
}
