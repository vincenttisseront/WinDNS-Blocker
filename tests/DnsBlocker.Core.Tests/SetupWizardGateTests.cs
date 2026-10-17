using DnsBlocker.Core.Models;
using DnsBlocker.Core.Paths;
using DnsBlocker.Core.Setup;

namespace DnsBlocker.Core.Tests;

public class SetupWizardGateTests
{
    [Fact]
    public void IsConfigIncomplete_WhenOnlyLocalhost()
    {
        var cfg = new AppConfig { DnsTargets = ["localhost"], SetupWizardCompleted = true };
        Assert.True(SetupWizardGate.IsConfigIncomplete(cfg));
    }

    [Fact]
    public void IsConfigIncomplete_WhenRealFarm()
    {
        var cfg = new AppConfig
        {
            DnsTargets = ["dns1.contoso.local"],
            BlocklistsUrl = "https://example.com/blocklists.txt",
            SetupWizardCompleted = true
        };
        Assert.False(SetupWizardGate.IsConfigIncomplete(cfg));
    }

    [Fact]
    public void IsConfigIncomplete_CuratedOnlyWithoutSteveJenkinsLol_IsComplete()
    {
        var cfg = new AppConfig
        {
            DnsTargets = ["dns1.contoso.local"],
            BlocklistsUrl = "",
            CuratedOisdBig = true,
            SetupWizardCompleted = true
        };
        Assert.True(SetupWizardGate.HasAnyBlocklistSource(cfg));
        Assert.False(SetupWizardGate.IsConfigIncomplete(cfg));
    }

    [Fact]
    public void IsConfigIncomplete_NoSourcesAtAll_IsIncomplete()
    {
        var cfg = new AppConfig
        {
            DnsTargets = ["dns1.contoso.local"],
            BlocklistsUrl = "",
            CuratedYoutubeAds = false,
            CuratedOisdBig = false,
            CuratedOisdNsfw = false,
            CuratedChadmayfieldPorn = false,
            ExtraBlocklistUrls = [],
            SetupWizardCompleted = true
        };
        Assert.True(SetupWizardGate.IsConfigIncomplete(cfg));
    }

    [Fact]
    public void ShouldShowWizard_ForceAlways()
    {
        var cfg = new AppConfig
        {
            DnsTargets = ["dns1.contoso.local"],
            BlocklistsUrl = "https://example.com/b.txt",
            SetupWizardCompleted = true
        };
        Assert.True(SetupWizardGate.ShouldShowWizard(cfg, force: true));
    }

    [Fact]
    public void IsLocalOnlyTarget_RecognizesLoopbacks()
    {
        Assert.True(SetupWizardGate.IsLocalOnlyTarget("127.0.0.1"));
        Assert.True(SetupWizardGate.IsLocalOnlyTarget("LocalHost"));
        Assert.False(SetupWizardGate.IsLocalOnlyTarget("dns.contoso.local"));
    }
}
