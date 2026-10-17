using DnsBlocker.Core.Dns;
using DnsBlocker.Core.Models;

namespace DnsBlocker.Core.Tests;

public class PolicyProbeTests
{
    [Fact]
    public async Task MockProvider_ListPolicies_ReportsEnabledFlags()
    {
        var provider = new MockDnsPolicyProvider();
        await provider.ApplyPoliciesAsync(
            "windc05.ar-systems.fr",
            "DnsBlocker_",
            "DENY",
            [["ads.example.com"], ["tracker.evil"]]);

        provider.SetPolicyEnabled("windc05.ar-systems.fr", "DnsBlocker_00002", enabled: false);

        var policies = await provider.ListPoliciesAsync("windc05.ar-systems.fr", "DnsBlocker_");
        Assert.Equal(2, policies.Count);
        Assert.True(policies[0].IsEnabled);
        Assert.False(policies[1].IsEnabled);
        Assert.Equal(["DnsBlocker_00001", "DnsBlocker_00002"], await provider.ListPolicyNamesAsync("windc05.ar-systems.fr", "DnsBlocker_"));
    }

    [Fact]
    public async Task MockProvider_ProbeFailure_Throws()
    {
        var provider = new MockDnsPolicyProvider();
        provider.ProbeFailingTargets.Add("dns-bad.local");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ListPoliciesAsync("dns-bad.local", "DnsBlocker_"));
    }

    [Fact]
    public void TargetPolicyStatus_AppliedLabel_ReflectsLiveVsSync()
    {
        var ok = new TargetPolicyStatus
        {
            Target = "windc05.ar-systems.fr",
            LastSyncStatus = TargetSyncStatus.Succeeded,
            LastSyncPolicyCount = 2,
            LivePolicyCount = 2,
            LiveEnabledCount = 2,
            ProbeOk = true,
            CheckedAt = DateTimeOffset.UtcNow
        };
        Assert.True(ok.AppliedOk);
        Assert.Equal("Appliquées", ok.AppliedLabel);

        var mismatch = new TargetPolicyStatus
        {
            Target = "windc05.ar-systems.fr",
            LastSyncStatus = TargetSyncStatus.Succeeded,
            LastSyncPolicyCount = 5,
            LivePolicyCount = 2,
            LiveEnabledCount = 2,
            ProbeOk = true,
            CheckedAt = DateTimeOffset.UtcNow
        };
        Assert.False(mismatch.AppliedOk);
        Assert.Equal("Écart sync / live", mismatch.AppliedLabel);

        var disabled = new TargetPolicyStatus
        {
            Target = "windc05.ar-systems.fr",
            LastSyncStatus = TargetSyncStatus.Succeeded,
            LastSyncPolicyCount = 2,
            LivePolicyCount = 2,
            LiveEnabledCount = 1,
            ProbeOk = true,
            CheckedAt = DateTimeOffset.UtcNow
        };
        Assert.Equal("Partiellement désactivées", disabled.AppliedLabel);
    }

    [Theory]
    [InlineData("""[{"Name":"DnsBlocker_0001","IsEnabled":true,"Action":"DENY"},{"Name":"DnsBlocker_0002","IsEnabled":false,"Action":"DENY"}]""", 2, 1)]
    [InlineData("""{"Name":"DnsBlocker_0001","IsEnabled":true,"Action":"IGNORE"}""", 1, 1)]
    [InlineData("[]", 0, 0)]
    [InlineData("", 0, 0)]
    public void ParsePolicyInventory_ReadsPowerShellJson(string json, int expectedCount, int expectedEnabled)
    {
        var policies = PowerShellDnsPolicyProvider.ParsePolicyInventory(json);
        Assert.Equal(expectedCount, policies.Count);
        Assert.Equal(expectedEnabled, policies.Count(p => p.IsEnabled));
    }
}
