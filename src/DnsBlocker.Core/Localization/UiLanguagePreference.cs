using System.Globalization;

namespace DnsBlocker.Core.Localization;

/// <summary>
/// Pure helpers for UI language preference (auto / en / fr).
/// UI project applies culture + resource dictionaries on top of this.
/// </summary>
public static class UiLanguagePreference
{
    public const string Auto = "auto";
    public const string English = "en";
    public const string French = "fr";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Auto;
        var v = value.Trim().ToLowerInvariant();
        if (v is Auto or "system" or "default")
            return Auto;
        if (v.StartsWith("fr", StringComparison.Ordinal))
            return French;
        if (v.StartsWith("en", StringComparison.Ordinal))
            return English;
        return Auto;
    }

    public static string ResolveEffective(string? preference, CultureInfo? osUi = null)
    {
        var pref = Normalize(preference);
        if (pref == French)
            return French;
        if (pref == English)
            return English;

        var culture = osUi ?? CultureInfo.CurrentUICulture;
        if (culture.Name.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
            return French;
        if (culture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase))
            return French;
        return English;
    }
}
