using DnsBlocker.Core.Dns;
using DnsBlocker.Core.Ingest;

namespace DnsBlocker.Core.Tests;

public class PowerShellDnsPolicyProviderTests
{
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
        Assert.Contains("windc05.ar-systems.fr", enriched, StringComparison.Ordinal);
    }

    [Fact]
    public void EnrichDnsPolicyError_MapsAccessDenied()
    {
        var enriched = PowerShellDnsPolicyProvider.EnrichDnsPolicyError("Access is denied. WIN32 5", "dns1");
        Assert.Contains("Access denied", enriched, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WinRM", enriched, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeChunks_DropsCommaDomains_AndRespectsBudget()
    {
        var input = new List<IReadOnlyList<string>>
        {
            new List<string> { "a.example.com", "bad,comma.com", "b.example.com" }
        };

        var chunks = PowerShellDnsPolicyProvider.NormalizeChunks(input, maxFqdnCriterionChars: 40);
        var flat = chunks.SelectMany(c => c).ToList();
        Assert.DoesNotContain(flat, d => d.Contains(','));
        Assert.Contains("a.example.com", flat);
        Assert.Contains("b.example.com", flat);
        Assert.All(chunks, c =>
        {
            var criterion = "EQ," + string.Join(",", c);
            Assert.True(criterion.Length <= 40 || c.Count == 1);
        });
    }

    [Fact]
    public void ChunkByFqdnBudget_SplitsWhenNearLimit()
    {
        var domains = Enumerable.Range(1, 50).Select(i => $"d{i:D2}.example.com");
        var chunks = DomainListParser.ChunkByFqdnBudget(domains, maxFqdnCriterionChars: 80, maxDomainsPerChunk: 100);
        Assert.True(chunks.Count > 1);
        Assert.Equal(50, chunks.Sum(c => c.Count));
    }
}
