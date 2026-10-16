using System.Globalization;
using DnsBlocker.Core.Localization;

namespace DnsBlocker.Core.Tests;

public class UiLanguagePreferenceTests
{
    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData("auto", "auto")]
    [InlineData("system", "auto")]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("fr", "fr")]
    [InlineData("fr-FR", "fr")]
    [InlineData("FR", "fr")]
    [InlineData("de", "auto")]
    public void Normalize_MapsKnownValues(string? input, string expected)
    {
        Assert.Equal(expected, UiLanguagePreference.Normalize(input));
    }

    [Fact]
    public void ResolveEffective_RespectsExplicitFrench()
    {
        var os = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("fr", UiLanguagePreference.ResolveEffective("fr", os));
        Assert.Equal("fr", UiLanguagePreference.ResolveEffective("fr-BE", os));
    }

    [Fact]
    public void ResolveEffective_RespectsExplicitEnglish()
    {
        var os = CultureInfo.GetCultureInfo("fr-FR");
        Assert.Equal("en", UiLanguagePreference.ResolveEffective("en", os));
        Assert.Equal("en", UiLanguagePreference.ResolveEffective("en-GB", os));
    }

    [Fact]
    public void ResolveEffective_AutoFollowsOsFrench()
    {
        Assert.Equal("fr", UiLanguagePreference.ResolveEffective("auto", CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal("fr", UiLanguagePreference.ResolveEffective(null, CultureInfo.GetCultureInfo("fr-CA")));
    }

    [Fact]
    public void ResolveEffective_AutoDefaultsToEnglish()
    {
        Assert.Equal("en", UiLanguagePreference.ResolveEffective("auto", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("en", UiLanguagePreference.ResolveEffective("auto", CultureInfo.GetCultureInfo("de-DE")));
    }
}
