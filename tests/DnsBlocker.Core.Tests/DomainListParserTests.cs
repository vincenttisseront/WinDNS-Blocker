using DnsBlocker.Core.Ingest;

namespace DnsBlocker.Core.Tests;

public class DomainListParserTests
{
    [Fact]
    public void ParseListOfLists_ExtractsHttpUrls_IgnoresComments()
    {
        var content = """
            # PI-HOLE BLOCKLISTS
            https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts
            http://example.com/list.txt # trailing comment

            not-a-url
            """;

        var urls = DomainListParser.ParseListOfLists(content);
        Assert.Equal(2, urls.Count);
        Assert.Contains(urls, u => u.Contains("StevenBlack", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseDomains_HostsAndPlainFormats()
    {
        var content = """
            # comment
            0.0.0.0 ads.example.com
            127.0.0.1 tracker.test.org
            plain-bad.domain.net
            localhost
            0.0.0.0
            """;

        var domains = DomainListParser.ParseDomains(content);
        Assert.Contains("ads.example.com", domains);
        Assert.Contains("tracker.test.org", domains);
        Assert.Contains("plain-bad.domain.net", domains);
        Assert.DoesNotContain("localhost", domains);
    }

    [Fact]
    public void ParseDomains_AbpAndDomainswildFormats()
    {
        var content = """
            [Adblock Plus]
            ! Title: sample
            ||ads.example.com^
            ||tracker.test.org^$third-party
            *.cdn.evil.net
            # comment
            plain.ok.com
            """;

        var domains = DomainListParser.ParseDomains(content);
        Assert.Contains("ads.example.com", domains);
        Assert.Contains("tracker.test.org", domains);
        Assert.Contains("*.cdn.evil.net", domains);
        Assert.Contains("plain.ok.com", domains);
    }

    [Fact]
    public void ApplyWhitelist_RemovesExactAndWildcardParents()
    {
        var blocked = new[] { "a.ads.com", "safe.example.com", "evil.com" };
        var white = new[] { "safe.example.com", "*.ads.com" };
        var result = DomainListParser.ApplyWhitelist(blocked, white);
        Assert.DoesNotContain("safe.example.com", result);
        Assert.DoesNotContain("a.ads.com", result);
        Assert.Contains("evil.com", result);
    }

    [Fact]
    public void EnsureApexForWildcards_AddsBareApex_ForBeegStyleLists()
    {
        var expanded = DomainListParser.EnsureApexForWildcards(["*.beeg.com", "*.cdn.evil.net", "ads.exact.com"]);
        Assert.Contains("*.beeg.com", expanded);
        Assert.Contains("beeg.com", expanded);
        Assert.Contains("*.cdn.evil.net", expanded);
        Assert.Contains("cdn.evil.net", expanded);
        Assert.Contains("ads.exact.com", expanded);
        Assert.DoesNotContain("*.ads.exact.com", expanded);
    }

    [Fact]
    public void Chunk_SplitsDeterministically()
    {
        var domains = Enumerable.Range(1, 25).Select(i => $"d{i:D2}.example.com");
        var chunks = DomainListParser.Chunk(domains, 10);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(10, chunks[0].Count);
        Assert.Equal(10, chunks[1].Count);
        Assert.Equal(5, chunks[2].Count);
    }
}
