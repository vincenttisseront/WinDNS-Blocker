using System.Globalization;

namespace DnsBlocker.UI.Localization;

/// <summary>Lookup localized UI strings (code-behind + DynamicResource catalog).</summary>
internal static class Loc
{
    private static string _lang = UiCulture.English;

    public static void SetLanguage(string language)
    {
        _lang = language == UiCulture.French ? UiCulture.French : UiCulture.English;
    }

    public static string T(string key)
    {
        if (LocCatalog.Entries.TryGetValue(key, out var pair))
            return _lang == UiCulture.French ? pair.Fr : pair.En;
        return key;
    }

    public static string F(string key, params object?[] args)
    {
        var fmt = T(key);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, fmt, args);
        }
        catch (FormatException)
        {
            return fmt;
        }
    }
}
