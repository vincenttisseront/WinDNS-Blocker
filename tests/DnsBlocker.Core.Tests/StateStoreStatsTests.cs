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
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }
}
