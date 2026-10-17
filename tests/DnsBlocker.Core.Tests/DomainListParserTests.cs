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
    public void ParseDomains_StripsHostsSinkholePrefixes_VincentWindc04()
    {
        // Smoking gun: QRP Criteria was EQ,0.0.0.0.creative.hpyrdr.com. — never matched queries.
        var content = """
            # StevenBlack / pi-hole hosts
            0.0.0.0 beeg.com
            0.0.0.0 ads.example.com
            127.0.0.1 tracker.evil.net
            ::1 localhost
            # Glued (no space) — seen in wild / bad exports
            0.0.0.0.creative.hpyrdr.com.
            0.0.0.0.hpyrdr.com.
            127.0.0.1.malware.test.
            0.ackzany.com.
            0.blissfullyzebra.com.
            """;

        var domains = DomainListParser.ParseDomains(content);
        Assert.Contains("beeg.com", domains);
        Assert.Contains("ads.example.com", domains);
        Assert.Contains("tracker.evil.net", domains);
        Assert.Contains("creative.hpyrdr.com", domains);
        Assert.Contains("hpyrdr.com", domains);
        Assert.Contains("malware.test", domains);
        Assert.Contains("ackzany.com", domains);
        Assert.Contains("blissfullyzebra.com", domains);

        Assert.DoesNotContain(domains, d => d.StartsWith("0.0.0.0.", StringComparison.Ordinal));
        Assert.DoesNotContain(domains, d => d.StartsWith("127.0.0.1.", StringComparison.Ordinal));
        Assert.DoesNotContain(domains, d => d.EndsWith('.'));
        Assert.DoesNotContain("0.0.0.0.creative.hpyrdr.com", domains);
        Assert.DoesNotContain("0.ackzany.com", domains);
        Assert.DoesNotContain("localhost", domains);
    }

    [Fact]
    public void TryNormalizeDomain_RejectsIpAndSinkholeJunk()
    {
        Assert.False(DomainListParser.TryNormalizeDomain("0.0.0.0", out _));
        Assert.False(DomainListParser.TryNormalizeDomain("127.0.0.1", out _));
        Assert.True(DomainListParser.TryNormalizeDomain("0.0.0.0.beeg.com.", out var beeg));
        Assert.Equal("beeg.com", beeg);
        Assert.True(DomainListParser.TryNormalizeDomain("Beeg.COM.", out var beeg2));
        Assert.Equal("beeg.com", beeg2);
    }

    [Fact]
    public void PrepareForQrpInPlace_KeepsBeegApexWithoutIpPrefix()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "*.beeg.com",
            "www.beeg.com",
            "0.0.0.0.beeg.com" // if somehow present before normalize path
        };
        // Normalize polluting entry the same way ingest does
        Assert.True(DomainListParser.TryNormalizeDomain("0.0.0.0.beeg.com", out var fixedBeeg));
        set.Remove("0.0.0.0.beeg.com");
        set.Add(fixedBeeg);
        DomainListParser.PrepareForQrpInPlace(set);
        Assert.Contains("*.beeg.com", set);
        Assert.Contains("beeg.com", set);
        Assert.DoesNotContain("www.beeg.com", set);
        Assert.DoesNotContain("0.0.0.0.beeg.com", set);
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

    [Fact]
    public void ChunkByFqdnBudget_RespectsCharLimitAndDomainCap()
    {
        var domains = Enumerable.Range(1, 50).Select(i => $"host{i:D3}.blocked.test");
        var chunks = DomainListParser.ChunkByFqdnBudget(domains, maxFqdnCriterionChars: 120, maxDomainsPerChunk: 10);
        Assert.True(chunks.Count >= 5);
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Count <= 10);
            var fqdn = "EQ," + string.Join(",", chunk);
            Assert.True(fqdn.Length <= 120, fqdn);
        }
    }

    [Fact]
    public void CompactCoveredByWildcards_DropsHostsUnderWildcard_KeepsApex()
    {
        var compacted = DomainListParser.CompactCoveredByWildcards([
            "*.beeg.com",
            "beeg.com",
            "www.beeg.com",
            "cdn.beeg.com",
            "ads.other.com"
        ]);
        Assert.Contains("*.beeg.com", compacted);
        Assert.Contains("beeg.com", compacted);
        Assert.DoesNotContain("www.beeg.com", compacted);
        Assert.DoesNotContain("cdn.beeg.com", compacted);
        Assert.Contains("ads.other.com", compacted);
    }

    [Fact]
    public void ParseDomainsInto_RespectsMaxDomainsCap()
    {
        var content = """
            a.example.com
            b.example.com
            c.example.com
            d.example.com
            """;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(content);
        var capped = DomainListParser.ParseDomainsInto(reader, set, maxDomains: 2);
        Assert.True(capped);
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void EnforceMaxDomains_TrimsToBudget()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "z.example.com", "a.example.com", "m.example.com", "b.example.com"
        };
        DomainListParser.EnforceMaxDomains(set, 2);
        Assert.Equal(2, set.Count);
        Assert.Contains("a.example.com", set);
        Assert.Contains("b.example.com", set);
    }
}
