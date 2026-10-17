using DnsBlocker.Core.Dns;

namespace DnsBlocker.Core.Tests;

public class PowerShellDnsPolicyProviderTests
{
    [Fact]
    public void BuildApplyScript_UsesManifestRunner_WithProcessingOrderRestartAndVerify()
    {
        var script = PowerShellDnsPolicyProvider.BuildApplyScript(
            "windc05.ar-systems.fr",
            "DnsBlocker_",
            "IGNORE",
            [["ads.example.com"], ["tracker.evil"]],
            restartDnsAfterApply: true);

        Assert.Contains("Remove-DnsBlockerPolicies", script, StringComparison.Ordinal);
        Assert.Contains("Add-DnsBlockerPolicy", script, StringComparison.Ordinal);
        Assert.Contains("-ProcessingOrder", script, StringComparison.Ordinal);
        Assert.Contains("-PassThru", script, StringComparison.Ordinal);
        Assert.Contains("Test-DnsBlockerHasFqdnCriteria", script, StringComparison.Ordinal);
        Assert.Contains("Clear-DnsServerCache", script, StringComparison.Ordinal);
        Assert.Contains("Restart-DnsBlockerDnsServer", script, StringComparison.Ordinal);
        Assert.Contains("VERIFY installed=", script, StringComparison.Ordinal);
        Assert.Contains("SMOKE_FAIL", script, StringComparison.Ordinal);
        Assert.Contains("Add-DnsServerQueryResolutionPolicy -ComputerName $Computer", script, StringComparison.Ordinal);
        // NDJSON stream + DC throttle (no full ConvertFrom-Json of multi-MB policies array).
        Assert.Contains("StreamReader", script, StringComparison.Ordinal);
        Assert.Contains("[GC]::Collect()", script, StringComparison.Ordinal);
        Assert.Contains("throttleMs", script, StringComparison.Ordinal);
        var manifest = ReadManifestFromScriptComment(script);
        Assert.Contains("\"local\":false", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("windc05.ar-systems.fr", manifest, StringComparison.Ordinal);
        Assert.Contains("IGNORE", manifest, StringComparison.Ordinal);
        Assert.Contains("\"throttleMs\":", manifest, StringComparison.Ordinal);
        // Header + one policy per line (not a single JSON array).
        Assert.Contains('\n', manifest);
    }

    [Fact]
    public void BuildApplyScript_LocalTarget_SetsLocalFlagAndRestartsService()
    {
        var script = PowerShellDnsPolicyProvider.BuildApplyScript(
            "localhost",
            "DnsBlocker_",
            "IGNORE",
            [["ads.example.com"]],
            restartDnsAfterApply: true);

        Assert.Contains("Add-DnsServerQueryResolutionPolicy -Name $Name -Action $Action -Fqdn $Fqdn -ProcessingOrder $Order -PassThru", script, StringComparison.Ordinal);
        Assert.Contains("Restart-Service -Name 'DNS' -Force", script, StringComparison.Ordinal);
        Assert.Contains("Clear-DnsServerCache -Force -ErrorAction SilentlyContinue", script, StringComparison.Ordinal);
        var manifest = ReadManifestFromScriptComment(script);
        Assert.Contains("\"local\":true", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"restartDns\":true", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildApplyScript_CanSkipDnsRestart()
    {
        var script = PowerShellDnsPolicyProvider.BuildApplyScript(
            "localhost",
            "DnsBlocker_",
            "DENY",
            [["a.example.com"]],
            restartDnsAfterApply: false);

        Assert.Contains("DNS_RESTART_SKIPPED", script, StringComparison.Ordinal);
        Assert.Contains("$restartDns = [bool]$manifest.restartDns", script, StringComparison.Ordinal);
        Assert.Contains("[GC]::Collect()", script, StringComparison.Ordinal);
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
    }

    [Fact]
    public void FormatPolicyName_UsesD5()
    {
        Assert.Equal("DnsBlocker_00001", PowerShellDnsPolicyProvider.FormatPolicyName("DnsBlocker_", 1));
        Assert.Equal("DnsBlocker_10000", PowerShellDnsPolicyProvider.FormatPolicyName("DnsBlocker_", 10000));
    }

    [Fact]
    public void PreferSmokeDomain_PrefersBeegWhenPresent()
    {
        var smoke = PowerShellDnsPolicyProvider.PreferSmokeDomain([
            ["aaa.example", "*.beeg.com", "beeg.com"],
            ["zzz.test"]
        ]);
        Assert.Equal("beeg.com", smoke);
    }

    [Fact]
    public void BuildSentinelFqdn_PinsApexAndWildcard_MicrosoftShape()
    {
        var fqdn = PowerShellDnsPolicyProvider.BuildSentinelFqdn([
            ["ads.example.com", "beeg.com", "*.beeg.com"]
        ]);
        Assert.Equal("EQ,beeg.com,*.beeg.com", fqdn);
        Assert.Equal(2, PowerShellDnsPolicyProvider.CountExpectedPolicies([
            ["ads.example.com", "beeg.com"]
        ]));
    }

    [Fact]
    public void BuildApplyScript_IncludesCanaryAndRejectsLoopbackOnlySmoke()
    {
        var script = PowerShellDnsPolicyProvider.BuildApplyScript(
            "windc04.ar-systems.fr",
            "DnsBlocker_",
            "IGNORE",
            [["beeg.com", "*.beeg.com"]],
            restartDnsAfterApply: true);

        Assert.Contains("CANARY_OK", script, StringComparison.Ordinal);
        Assert.Contains("www.microsoft.com", script, StringComparison.Ordinal);
        Assert.Contains("serverCandidates", script, StringComparison.Ordinal);
        Assert.Contains("unexpected resolve error", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Enabled QRP count is 0", script, StringComparison.Ordinal);
        var manifest = ReadManifestFromScriptComment(script);
        Assert.Contains("EQ,beeg.com,*.beeg.com", manifest, StringComparison.Ordinal);
        Assert.Contains("\"expected\":2", manifest, StringComparison.Ordinal); // sentinel + 1 chunk
    }

    [Fact]
    public void BuildApplyScript_FullReplace_RemovesAllPrefixThenVerifiesZero()
    {
        var script = PowerShellDnsPolicyProvider.BuildApplyScript(
            "localhost",
            "DnsBlocker_",
            "IGNORE",
            [["beeg.com"]],
            restartDnsAfterApply: false);

        Assert.Contains("Remove-DnsBlockerPolicies", script, StringComparison.Ordinal);
        Assert.Contains("REMOVED count=", script, StringComparison.Ordinal);
        Assert.Contains("REMOVED verify=0", script, StringComparison.Ordinal);
        Assert.Contains("Remove incomplete", script, StringComparison.Ordinal);
        Assert.Contains("Get-DnsBlockerFqdnCriterionText", script, StringComparison.Ordinal);
        Assert.Contains("CRITERIA_READBACK", script, StringComparison.Ordinal);
        Assert.Contains("CRITERIA_SEARCH_OK", script, StringComparison.Ordinal);
        Assert.Contains("CRITERIA_SEARCH_FAIL", script, StringComparison.Ordinal);
        Assert.Contains("Find-DnsBlockerSmokeInCriteria", script, StringComparison.Ordinal);
        // Prefix glob must wipe D4 DnsBlocker_0001 Deny leftovers and Probe_Beeg
        Assert.Contains("$_.Name -like ($Prefix + '*')", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateApplyOutput_ThrowsOnSmokeSkipWithPolicies()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PowerShellDnsPolicyProvider.ValidateApplyOutput(
                "VERIFY installed=1 enabled=1 expected=1 smoke=SKIP\n",
                "windc04",
                1));
        Assert.Contains("SKIP", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeChunks_RespectsFqdnBudget()
    {
        // 100 domains of 40 chars would exceed 8192 if packed naively at 1000/chunk
        var domains = Enumerable.Range(0, 500)
            .Select(i => $"very-long-blocked-hostname-{i:D4}.example-blocklist.test")
            .ToList();
        var chunks = PowerShellDnsPolicyProvider.NormalizeChunks([domains], PowerShellDnsPolicyProvider.MaxFqdnCriterionChars);

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            var fqdn = "EQ," + string.Join(",", chunk);
            Assert.True(fqdn.Length <= PowerShellDnsPolicyProvider.MaxFqdnCriterionChars,
                $"Criterion length {fqdn.Length} exceeds budget");
        }
    }

    [Fact]
    public void ValidateApplyOutput_AcceptsMatchingVerifyLine()
    {
        PowerShellDnsPolicyProvider.ValidateApplyOutput(
            "OK added=2\nCACHE_CLEARED\nDNS_RESTARTED\nVERIFY installed=2 enabled=2 expected=2 smoke=OK\nDONE policies=2\n",
            "windc04",
            2);
    }

    [Fact]
    public void ValidateApplyOutput_ThrowsOnEnabledMismatch()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PowerShellDnsPolicyProvider.ValidateApplyOutput(
                "VERIFY installed=10 enabled=0 expected=10 smoke=SKIP\n",
                "windc04",
                10));
        Assert.Contains("enabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateApplyOutput_ThrowsOnSmokeFail()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PowerShellDnsPolicyProvider.ValidateApplyOutput(
                "VERIFY installed=1 enabled=1 expected=1 smoke=FAIL\n",
                "windc04",
                1));
        Assert.Contains("smoke", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateApplyOutput_ThrowsOnEmptyInstalledEquals_HashtableUnwrapBug()
    {
        // Vincent 1.0.28 windc04: PS `return @{installed=…}` unwrapped → VERIFY installed= enabled=
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PowerShellDnsPolicyProvider.ValidateApplyOutput(
                "VERIFY installed= enabled= expected=2002 smoke=OK\n",
                "windc04.ar-systems.fr",
                2002));
        Assert.Contains("installed=", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildApplyScript_LiveCountsReturnPsCustomObjectNotHashtable()
    {
        var script = PowerShellDnsPolicyProvider.BuildApplyScript(
            "windc04", "DnsBlocker_", "IGNORE", [["beeg.com"]], restartDnsAfterApply: false);
        Assert.Contains("Get-DnsBlockerPolicyInventory", script, StringComparison.Ordinal);
        Assert.Contains("COUNT_SOURCE source=", script, StringComparison.Ordinal);
        Assert.DoesNotContain("return @{ installed", script);
        Assert.Contains("$installedLive = [int]$counts.installed", script);
        Assert.Contains("VERIFY installed=$installedLive enabled=$enabledLive", script);
        Assert.Contains("sentinel-scan", script, StringComparison.Ordinal);
        Assert.Contains(@"DNS Server\Policies", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateApplyOutput_ThrowsOnSmokeOkWithInstalledZero()
    {
        // Vincent 1.0.31 windc04: VERIFY installed=0 enabled=0 expected=2002 smoke=OK must never Succeeded.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PowerShellDnsPolicyProvider.ValidateApplyOutput(
                "VERIFY installed=0 enabled=0 expected=2002 smoke=OK\n",
                "windc04.ar-systems.fr",
                2002));
        Assert.Contains("installed=0", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will not block", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateApplyOutput_PrefersMatchingVerifyLineOverStaleZero()
    {
        PowerShellDnsPolicyProvider.ValidateApplyOutput(
            "VERIFY installed=0 enabled=0 expected=2 smoke=FAIL\n" +
            "VERIFY installed=2 enabled=2 expected=2 smoke=OK\nDONE policies=2\n",
            "windc05",
            2);
    }

    [Fact]
    public void BuildApplyScript_EmbedsInventoryHelperFallbacks()
    {
        var script = PowerShellDnsPolicyProvider.BuildApplyScript(
            "windc05.ar-systems.fr", "DnsBlocker_", "IGNORE", [["beeg.com"]], restartDnsAfterApply: false);
        Assert.Contains("Get-DnsBlockerPolicyInventory", script, StringComparison.Ordinal);
        Assert.Contains("name-wildcard", script, StringComparison.Ordinal);
        Assert.Contains("sentinel-scan", script, StringComparison.Ordinal);
        Assert.Contains("Test-DnsBlockerNameMatch", script, StringComparison.Ordinal);
        Assert.Contains("COUNT_SOURCE source=", script, StringComparison.Ordinal);
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
    public void EnrichDnsPolicyError_MapsSmokeOrEmptyCriteria()
    {
        var enriched = PowerShellDnsPolicyProvider.EnrichDnsPolicyError(
            "SMOKE_FAIL: beeg.com still resolves", "windc04");
        Assert.Contains("verification failed", enriched, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("windc04", enriched, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MockProvider_ApplyPolicies_ReplacesPriorChunksIdempotently()
    {
        var provider = new MockDnsPolicyProvider();
        await provider.ApplyPoliciesAsync("windc05", "DnsBlocker_", "DENY",
            [["a.com"], ["b.com"], ["c.com"]]);

        await provider.ApplyPoliciesAsync("windc05", "DnsBlocker_", "DENY",
            [["a.com"], ["b.com"]]);

        var names = await provider.ListPolicyNamesAsync("windc05", "DnsBlocker_");
        Assert.Equal(["DnsBlocker_00001", "DnsBlocker_00002"], names);
        Assert.DoesNotContain(names, n => n.EndsWith("00003", StringComparison.Ordinal));
    }

    private static string ReadManifestFromScriptComment(string script)
    {
        var line = script.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("# manifest=", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(line));
        var path = line!["# manifest=".Length..].Trim();
        Assert.True(File.Exists(path), $"Manifest missing: {path}");
        return File.ReadAllText(path);
    }
}
