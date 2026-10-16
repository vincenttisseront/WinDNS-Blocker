using DnsBlocker.Core.Dns;

namespace DnsBlocker.Core.Tests;

public class PowerShellDnsPolicyProviderTests
{
    [Fact]
    public void BuildApplyScript_RemovesAllPrefixedPoliciesBeforeAdd()
    {
        var script = PowerShellDnsPolicyProvider.BuildApplyScript(
            "windc05.ar-systems.fr",
            "DnsBlocker_",
            "DENY",
            [["ads.example.com"], ["tracker.evil"]]);

        Assert.Contains("Remove-DnsBlockerPolicies", script, StringComparison.Ordinal);
        Assert.Contains("Add-DnsBlockerPolicy", script, StringComparison.Ordinal);
        Assert.Contains("DnsBlocker_0001", script, StringComparison.Ordinal);
        Assert.Contains("DnsBlocker_0002", script, StringComparison.Ordinal);
        Assert.Contains("windc05.ar-systems.fr", script, StringComparison.Ordinal);
        Assert.Contains("Clear-DnsServerCache", script, StringComparison.Ordinal);

        var removeIdx = script.IndexOf("Remove-DnsBlockerPolicies -Computer $computer -Prefix $prefix", StringComparison.Ordinal);
        var addIdx = script.IndexOf("Add-DnsBlockerPolicy -Computer $computer -Name $name", StringComparison.Ordinal);
        var cacheIdx = script.IndexOf("Clear-DnsServerCache", StringComparison.Ordinal);
        Assert.True(removeIdx >= 0);
        Assert.True(addIdx > removeIdx, "Clean replace must remove all DnsBlocker_* before recreating chunks.");
        Assert.True(cacheIdx > addIdx, "DNS cache clear must run after policy recreate.");
    }

    [Fact]
    public void BuildApplyScript_RetriesAddOnWin32_9971AlreadyExists()
    {
        var script = PowerShellDnsPolicyProvider.BuildApplyScript(
            "dns1",
            "DnsBlocker_",
            "DENY",
            [["a.example.com"]]);

        Assert.Contains("Test-DnsBlockerPolicyAlreadyExists", script, StringComparison.Ordinal);
        Assert.Contains("9971", script, StringComparison.Ordinal);
        Assert.Contains("already exists", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DNS_ERROR_POLICY_ALREADY_EXISTS", script, StringComparison.Ordinal);
        // On already-exists: Remove then Add again for that chunk
        Assert.Contains(
            "Remove-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $Name -Force -ErrorAction SilentlyContinue",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnrichDnsPolicyError_MapsWin32_9971_ToIdempotentGuidance()
    {
        var raw =
            "Add-DnsServerQueryResolutionPolicy : The policy DnsBlocker_0001 already exists on DNS server windc05.ar-systems.fr. " +
            "FullyQualifiedErrorId : WIN32 9971,Add-DnsServerQueryResolutionPolicy";

        var enriched = PowerShellDnsPolicyProvider.EnrichDnsPolicyError(raw, "windc05.ar-systems.fr");

        Assert.Contains("WIN32 9971", enriched, StringComparison.Ordinal);
        Assert.Contains("already exists", enriched, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("windc05.ar-systems.fr", enriched, StringComparison.Ordinal);
        Assert.Contains("Remove-DnsServerQueryResolutionPolicy", enriched, StringComparison.Ordinal);
    }

    [Fact]
    public void EnrichDnsPolicyError_MapsWin32_1011_ToRegistryAclGuidance()
    {
        var raw =
            "Add-DnsServerQueryResolutionPolicy : Failed to create policy DnsBlocker_0001 on DNS server windc05.ar-systems.fr. " +
            "Please see internal exception for details. | WIN32 1011,Add-DnsServerQueryResolutionPolicy";

        var enriched = PowerShellDnsPolicyProvider.EnrichDnsPolicyError(raw, "windc05.ar-systems.fr");

        Assert.Contains("WIN32 1011", enriched, StringComparison.Ordinal);
        Assert.Contains("DNS Server", enriched, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DnsAdmins", enriched, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnrichDnsPolicyError_MapsAccessDenied()
    {
        var enriched = PowerShellDnsPolicyProvider.EnrichDnsPolicyError("Access is denied. WIN32 5", "dns1");
        Assert.Contains("Access denied", enriched, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WinRM", enriched, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MockProvider_ApplyPolicies_ReplacesPriorChunksIdempotently()
    {
        var provider = new MockDnsPolicyProvider();
        // Simulate partial prior sync: 3 policies already present
        await provider.ApplyPoliciesAsync("windc05", "DnsBlocker_", "DENY",
            [["a.com"], ["b.com"], ["c.com"]]);

        // Resync with fewer chunks must replace, not accumulate
        await provider.ApplyPoliciesAsync("windc05", "DnsBlocker_", "DENY",
            [["a.com"], ["b.com"]]);

        var names = await provider.ListPolicyNamesAsync("windc05", "DnsBlocker_");
        Assert.Equal(["DnsBlocker_0001", "DnsBlocker_0002"], names);
        Assert.DoesNotContain(names, n => n.EndsWith("0003", StringComparison.Ordinal));
    }
}
