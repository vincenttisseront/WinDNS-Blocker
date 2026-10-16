using DnsBlocker.Core.Storage;

namespace DnsBlocker.Core.Tests;

public class StateStoreStatsTests
{
    [Fact]
    public void GetStats_AggregatesBlockedAndClients()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "dnsblocker-stats-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var state = new StateStore(dbPath);
            state.ReplaceDomains(["ads.example.com", "tracker.evil"]);
            state.InsertQueryEvent(DateTimeOffset.UtcNow.AddMinutes(-10), "10.0.0.5", "ads.example.com", true);
            state.InsertQueryEvent(DateTimeOffset.UtcNow.AddMinutes(-5), "10.0.0.5", "ads.example.com", true);
            state.InsertQueryEvent(DateTimeOffset.UtcNow.AddMinutes(-2), "10.0.0.8", "ok.example.com", false);

            var stats = state.GetStats(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow);
            Assert.Equal(3, stats.TotalQueries);
            Assert.Equal(2, stats.BlockedQueries);
            Assert.Equal("ads.example.com", stats.TopBlockedDomains[0].Key);
            Assert.Equal(2, stats.TopBlockedDomains[0].Count);
            Assert.Equal("10.0.0.5", stats.TopClients[0].Key);
            Assert.Equal(2, stats.TopQueriedDomains.Count);
            Assert.Equal("ads.example.com", stats.TopQueriedDomains[0].Key);
            Assert.Equal("ok.example.com", stats.TopQueriedDomains[1].Key);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void GetStats_TopN_LimitsQueriedAndBlocked()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "dnsblocker-stats-topn-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var state = new StateStore(dbPath);
            for (var i = 0; i < 15; i++)
            {
                var domain = $"site{i:D2}.example.com";
                var blocked = i % 2 == 0;
                for (var hit = 0; hit <= i; hit++)
                    state.InsertQueryEvent(DateTimeOffset.UtcNow.AddMinutes(-hit), "10.0.0.1", domain, blocked);
            }

            var top10 = state.GetStats(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, topN: 10);
            Assert.Equal(10, top10.TopN);
            Assert.Equal(10, top10.TopQueriedDomains.Count);
            Assert.Equal("site14.example.com", top10.TopQueriedDomains[0].Key);
            Assert.True(top10.TopBlockedDomains.Count <= 10);
            Assert.All(top10.TopBlockedDomains, row => Assert.StartsWith("site", row.Key));

            var top20 = state.GetStats(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, topN: 20);
            Assert.Equal(15, top20.TopQueriedDomains.Count);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }
}
