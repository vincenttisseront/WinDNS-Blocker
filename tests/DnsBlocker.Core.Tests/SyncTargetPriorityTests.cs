using DnsBlocker.Core.Dns;
using DnsBlocker.Core.Ingest;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Storage;
using DnsBlocker.Core.Sync;
using RichardSzalay.MockHttp;

namespace DnsBlocker.Core.Tests;

public class SyncTargetPriorityTests
{
    [Fact]
    public async Task RunAsync_AppliesEmptyBeforePopulated_DefersExtraPopulated()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://lol.test/blocklists.txt")
            .Respond("text/plain", "https://lists.test/a.txt\n");
        mockHttp.When("https://lol.test/whitelists.txt")
            .Respond("text/plain", "# none\n");
        mockHttp.When("https://lists.test/a.txt")
            .Respond("text/plain", "ads.one.com\nads.two.com\n");

        var provider = new MockDnsPolicyProvider();
        // Seed one populated target; leave the other empty.
        await provider.ApplyPoliciesAsync(
            "dns-populated.local",
            "DnsBlocker_",
            "IGNORE",
            [["ads.old.com"]],
            restartDnsAfterApply: false);

        var applyOrder = new List<string>();
        var progress = new ImmediateProgress<SyncProgressUpdate>(u =>
        {
            if (u.Phase == SyncProgressPhase.ApplyTarget && u.Target is not null)
                applyOrder.Add(u.Target);
        });

        var dbPath = Path.Combine(Path.GetTempPath(), "dnsblocker-prio-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var state = new StateStore(dbPath);
            // Oldest success on a third populated host so round-robin picks it after empty.
            state.SaveSyncResult(new SyncRunResult
            {
                StartedAt = DateTimeOffset.UtcNow.AddDays(-2),
                FinishedAt = DateTimeOffset.UtcNow.AddDays(-2),
                DomainCount = 1,
                Targets =
                [
                    new TargetSyncResult
                    {
                        Target = "dns-stale.local",
                        Status = TargetSyncStatus.Succeeded,
                        PolicyCount = 1,
                        FinishedAt = DateTimeOffset.UtcNow.AddDays(-2)
                    },
                    new TargetSyncResult
                    {
                        Target = "dns-populated.local",
                        Status = TargetSyncStatus.Succeeded,
                        PolicyCount = 1,
                        FinishedAt = DateTimeOffset.UtcNow.AddHours(-1)
                    }
                ]
            });

            await provider.ApplyPoliciesAsync(
                "dns-stale.local",
                "DnsBlocker_",
                "IGNORE",
                [["ads.stale.com"]],
                restartDnsAfterApply: false);

            var engine = new SyncEngine(
                new BlocklistIngestor(mockHttp.ToHttpClient()),
                provider,
                state);

            var result = await engine.RunAsync(
                new AppConfig
                {
                    BlocklistsUrl = "https://lol.test/blocklists.txt",
                    WhitelistsUrl = "https://lol.test/whitelists.txt",
                    DnsTargets = ["dns-populated.local", "dns-empty.local", "dns-stale.local"],
                    CuratedYoutubeAds = false,
                    CuratedOisdBig = false,
                    CuratedOisdNsfw = false,
                    CuratedChadmayfieldPorn = false,
                    MaxDomains = 400_000,
                    MaxPopulatedTargetsPerSync = 1,
                    RestartDnsAfterPolicySync = false
                },
                progress: progress);

            Assert.Null(result.Error);
            Assert.True(result.Success);
            Assert.Equal(2, result.AppliedCount);
            Assert.Equal(1, result.DeferredCount);

            var empty = result.Targets.Single(t => t.Target == "dns-empty.local");
            var stale = result.Targets.Single(t => t.Target == "dns-stale.local");
            var populated = result.Targets.Single(t => t.Target == "dns-populated.local");

            Assert.Equal(TargetSyncStatus.Succeeded, empty.Status);
            Assert.Equal(TargetSyncStatus.Succeeded, stale.Status);
            Assert.Equal(TargetSyncStatus.Skipped, populated.Status);
            Assert.Contains("deferred", populated.Error ?? "", StringComparison.OrdinalIgnoreCase);

            // Empty first, then oldest populated; fresher populated never applied this run.
            Assert.Equal(["dns-empty.local", "dns-stale.local"], applyOrder);
            Assert.True(provider.Snapshot.ContainsKey("dns-empty.local"));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task RunAsync_UnlimitedPopulated_AppliesAll()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://lol.test/blocklists.txt")
            .Respond("text/plain", "https://lists.test/a.txt\n");
        mockHttp.When("https://lol.test/whitelists.txt")
            .Respond("text/plain", "# none\n");
        mockHttp.When("https://lists.test/a.txt")
            .Respond("text/plain", "ads.one.com\n");

        var provider = new MockDnsPolicyProvider();
        await provider.ApplyPoliciesAsync(
            "a.local", "DnsBlocker_", "IGNORE", [["x.com"]], restartDnsAfterApply: false);
        await provider.ApplyPoliciesAsync(
            "b.local", "DnsBlocker_", "IGNORE", [["y.com"]], restartDnsAfterApply: false);

        var dbPath = Path.Combine(Path.GetTempPath(), "dnsblocker-prio-all-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var state = new StateStore(dbPath);
            var engine = new SyncEngine(
                new BlocklistIngestor(mockHttp.ToHttpClient()),
                provider,
                state);

            var result = await engine.RunAsync(new AppConfig
            {
                BlocklistsUrl = "https://lol.test/blocklists.txt",
                WhitelistsUrl = "https://lol.test/whitelists.txt",
                DnsTargets = ["a.local", "b.local"],
                CuratedYoutubeAds = false,
                CuratedOisdBig = false,
                CuratedOisdNsfw = false,
                CuratedChadmayfieldPorn = false,
                MaxPopulatedTargetsPerSync = 0,
                RestartDnsAfterPolicySync = false
            });

            Assert.True(result.Success);
            Assert.Equal(0, result.DeferredCount);
            Assert.All(result.Targets, t => Assert.Equal(TargetSyncStatus.Succeeded, t.Status));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    private sealed class ImmediateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public ImmediateProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
