using DnsBlocker.Core.Dns;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Net;

namespace DnsBlocker.Core.Tests;

public class LocalDnsTargetTests : IDisposable
{
    public LocalDnsTargetTests()
    {
        LocalDnsTarget.DnsServerRunningOverride = null;
        LocalDnsTarget.PreferredNameOverride = null;
        LocalDnsTarget.LocalAliasesOverride = null;
    }

    public void Dispose()
    {
        LocalDnsTarget.DnsServerRunningOverride = null;
        LocalDnsTarget.PreferredNameOverride = null;
        LocalDnsTarget.LocalAliasesOverride = null;
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData(".")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void IsLocalTarget_RecognizesLoopbackAliases(string target)
    {
        Assert.True(LocalDnsTarget.IsLocalTarget(target));
    }

    [Fact]
    public void IsLocalTarget_MatchesOverrideAliases()
    {
        LocalDnsTarget.LocalAliasesOverride = () => ["windc04", "windc04.ar-systems.fr"];
        Assert.True(LocalDnsTarget.IsLocalTarget("windc04.ar-systems.fr"));
        Assert.True(LocalDnsTarget.IsLocalTarget("WINDC04"));
        Assert.False(LocalDnsTarget.IsLocalTarget("windc05.ar-systems.fr"));
    }

    [Fact]
    public void TryEnsureDefaultTargets_ReplacesScaffoldWhenDnsRunning()
    {
        LocalDnsTarget.DnsServerRunningOverride = () => true;
        LocalDnsTarget.PreferredNameOverride = () => "windc04.ar-systems.fr";

        var cfg = new AppConfig { DnsTargets = ["localhost"] };
        Assert.True(LocalDnsTarget.TryEnsureDefaultTargets(cfg));
        Assert.Equal(["windc04.ar-systems.fr"], cfg.DnsTargets);
    }

    [Fact]
    public void TryEnsureDefaultTargets_DoesNotRewriteExistingFarm()
    {
        LocalDnsTarget.DnsServerRunningOverride = () => true;
        LocalDnsTarget.PreferredNameOverride = () => "windc04.ar-systems.fr";

        var cfg = new AppConfig { DnsTargets = ["windc05.ar-systems.fr"] };
        Assert.False(LocalDnsTarget.TryEnsureDefaultTargets(cfg));
        Assert.Equal(["windc05.ar-systems.fr"], cfg.DnsTargets);
    }

    [Fact]
    public void TryEnsureDefaultTargets_NoOpWhenDnsNotRunning()
    {
        LocalDnsTarget.DnsServerRunningOverride = () => false;
        LocalDnsTarget.PreferredNameOverride = () => "windc04.ar-systems.fr";

        var cfg = new AppConfig { DnsTargets = ["localhost"] };
        Assert.False(LocalDnsTarget.TryEnsureDefaultTargets(cfg));
        Assert.Equal(["localhost"], cfg.DnsTargets);
    }

    [Fact]
    public void EnsureInTargets_InsertsPreferredNameAndDropsScaffold()
    {
        LocalDnsTarget.PreferredNameOverride = () => "windc04.contoso.local";
        var cfg = new AppConfig { DnsTargets = ["localhost", "windc05.contoso.local"] };
        var name = LocalDnsTarget.EnsureInTargets(cfg);
        Assert.Equal("windc04.contoso.local", name);
        Assert.Equal(["windc04.contoso.local", "windc05.contoso.local"], cfg.DnsTargets);
    }

    [Fact]
    public void BuildApplyScript_UsesLocalFlagForFqdnAlias()
    {
        LocalDnsTarget.LocalAliasesOverride = () => ["windc04.ar-systems.fr"];
        var script = PowerShellDnsPolicyProvider.BuildApplyScript(
            "windc04.ar-systems.fr",
            "DnsBlocker_",
            "DENY",
            [["a.example.com"]]);
        var manifestLine = script.Split('\n')
            .Select(l => l.Trim())
            .First(l => l.StartsWith("# manifest=", StringComparison.Ordinal));
        var path = manifestLine["# manifest=".Length..].Trim();
        var manifest = File.ReadAllText(path);
        Assert.Contains("\"local\":true", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("windc04.ar-systems.fr", manifest, StringComparison.Ordinal);
    }
}
