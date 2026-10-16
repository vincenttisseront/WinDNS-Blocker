using DnsBlocker.Core.Dns;
using DnsBlocker.Core.Ingest;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Storage;
using DnsBlocker.Core.Sync;
using RichardSzalay.MockHttp;

namespace DnsBlocker.Core.Tests;

public class SyncEngineTests
{
    [Fact]
    public async Task RunAsync_PushesToAllTargets_PartialFailurePreserved()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://lol.test/blocklists.txt")
            .Respond("text/plain", "https://lists.test/a.txt\n");
        mockHttp.When("https://lol.test/whitelists.txt")
            .Respond("text/plain", "# none\n");
        mockHttp.When("https://lists.test/a.txt")
            .Respond("text/plain", """
                0.0.0.0 ads.one.com
                0.0.0.0 ads.two.com
                0.0.0.0 keep.me.com
                """);

        var http = mockHttp.ToHttpClient();
        var ingestor = new BlocklistIngestor(http);
        var provider = new MockDnsPolicyProvider();
        provider.FailingTargets.Add("dns-bad.local");

        var dbPath = Path.Combine(Path.GetTempPath(), "dnsblocker-test-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var state = new StateStore(dbPath);
            var engine = new SyncEngine(ingestor, provider, state);
            var config = new AppConfig
            {
                BlocklistsUrl = "https://lol.test/blocklists.txt",
                WhitelistsUrl = "https://lol.test/whitelists.txt",
                DnsTargets = ["dns-ok.local", "dns-bad.local"],
                ChunkSize = 1000,
                MaxParallelTargets = 2,
                PolicyAction = "DENY"
            };

            var result = await engine.RunAsync(config);
            Assert.Null(result.Error);
            Assert.True(result.PartialSuccess);
            Assert.Equal(3, result.DomainCount);

            var ok = result.Targets.Single(t => t.Target == "dns-ok.local");
            var bad = result.Targets.Single(t => t.Target == "dns-bad.local");
            Assert.Equal(TargetSyncStatus.Succeeded, ok.Status);
            Assert.Equal(TargetSyncStatus.Failed, bad.Status);
            Assert.NotNull(bad.Error);

            Assert.True(provider.Snapshot.ContainsKey("dns-ok.local"));
            Assert.False(provider.Snapshot.ContainsKey("dns-bad.local"));
            Assert.Equal(3, state.CountDomains());
            Assert.Contains("ads.one.com", state.SearchDomains("ads"));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task RunAsync_AppliesWhitelist()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://lol.test/blocklists.txt")
            .Respond("text/plain", "https://lists.test/b.txt\n");
        mockHttp.When("https://lol.test/whitelists.txt")
            .Respond("text/plain", "https://lists.test/w.txt\n");
        mockHttp.When("https://lists.test/b.txt")
            .Respond("text/plain", "bad.example.com\ngood.example.com\n");
        mockHttp.When("https://lists.test/w.txt")
            .Respond("text/plain", "good.example.com\n");

        var provider = new MockDnsPolicyProvider();
        var dbPath = Path.Combine(Path.GetTempPath(), "dnsblocker-test-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var state = new StateStore(dbPath);
            var engine = new SyncEngine(new BlocklistIngestor(mockHttp.ToHttpClient()), provider, state);
            var result = await engine.RunAsync(new AppConfig
            {
                BlocklistsUrl = "https://lol.test/blocklists.txt",
                WhitelistsUrl = "https://lol.test/whitelists.txt",
                DnsTargets = ["localhost"]
            });

            Assert.Equal(1, result.DomainCount);
            Assert.Equal(TargetSyncStatus.Succeeded, result.Targets[0].Status);
            var domains = provider.Snapshot["localhost"][0].Domains;
            Assert.Contains("bad.example.com", domains);
            Assert.DoesNotContain("good.example.com", domains);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }
}
