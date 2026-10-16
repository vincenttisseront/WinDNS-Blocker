using System.Globalization;
using System.Windows;
using DnsBlocker.Core.Config;
using DnsBlocker.Core.Localization;
using DnsBlocker.Core.Models;

namespace DnsBlocker.UI.Localization;

/// <summary>
/// Resolves and applies the WPF UI culture from AppConfig / OS.
/// </summary>
internal static class UiCulture
{
    public const string Auto = UiLanguagePreference.Auto;
    public const string English = UiLanguagePreference.English;
    public const string French = UiLanguagePreference.French;

    /// <summary>Effective language code currently applied ("en" or "fr").</summary>
    public static string Effective { get; private set; } = English;

    public static string NormalizePreference(string? value) => UiLanguagePreference.Normalize(value);

    public static string ResolveEffective(string? preference, CultureInfo? osUi = null) =>
        UiLanguagePreference.ResolveEffective(preference, osUi);

    public static void ApplyFromConfig(AppConfig? config = null)
    {
        config ??= SafeLoadConfig();
        Apply(ResolveEffective(config.UiLanguage));
    }

    public static void Apply(string effectiveLanguage)
    {
        Effective = effectiveLanguage is French ? French : English;
        var culture = Effective == French
            ? CultureInfo.GetCultureInfo("fr-FR")
            : CultureInfo.GetCultureInfo("en-US");

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        Loc.SetLanguage(Effective);
        ApplyResourceDictionary(Effective);
    }

    public static AppConfig SafeLoadConfig()
    {
        try
        {
            return new ConfigStore().Load();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public static void PersistPreference(string preference, AppConfig? inMemory = null)
    {
        var normalized = NormalizePreference(preference);
        try
        {
            var store = new ConfigStore();
            var cfg = inMemory ?? store.Load();
            cfg.UiLanguage = normalized;
            store.Save(cfg);
        }
        catch
        {
            // UI still applies in-process; disk persist may fail on locked/missing ProgramData.
        }
    }

    private static void ApplyResourceDictionary(string lang)
    {
        if (Application.Current is null)
            return;

        var dict = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/Localization/Strings.{lang}.xaml",
                UriKind.Absolute)
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.OriginalString ?? "";
            if (src.Contains("Localization/Strings.", StringComparison.OrdinalIgnoreCase))
                merged.RemoveAt(i);
        }

        merged.Insert(0, dict);
    }
}
