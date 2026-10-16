using DnsBlocker.Core.Stats;
using DnsBlocker.Core.Storage;

namespace DnsBlocker.Core.Tests;

public class StatsDiagnosisTests
{
    [Fact]
    public void IsBlocked_MatchesWildcardApexAndSubdomain()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "dnsblocker-wildcard-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var state = new StateStore(dbPath);
            state.ReplaceDomains(["*.beeg.com", "beeg.com"]);

            Assert.True(state.IsBlocked("beeg.com"));
            Assert.True(state.IsBlocked("www.beeg.com"));
            Assert.True(state.IsBlocked("a.b.beeg.com"));
            Assert.False(state.IsBlocked("beeg.net"));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void BuildBlockGuidance_ExplainsClientDnsWhenResolved()
    {
        var guidance = DnsAnalyticalLogClient.BuildBlockGuidance(
            inBlocklist: true,
            appearsBlocked: false,
            target: "windc05.ar-systems.fr",
            domain: "beeg.com");

        Assert.Contains("windc05", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blocklist", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DoH", guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetStats_EmptyWindow_HasMessage()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "dnsblocker-empty-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var state = new StateStore(dbPath);
            var snap = state.GetStats(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow);
            Assert.Equal(0, snap.TotalQueries);
            Assert.Contains("No query events", snap.Message ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }
}
