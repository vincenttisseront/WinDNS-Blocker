using DnsBlocker.Core.Dns;
using DnsBlocker.Core.Ingest;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Storage;
using DnsBlocker.Core.Sync;
using RichardSzalay.MockHttp;

namespace DnsBlocker.Core.Tests;

public class SyncProgressTests
{
    [Theory]
    [InlineData("OK progress 50/200", 50, 200)]
    [InlineData("OK progress 25/100 (GC)", 25, 100)]
    [InlineData("  OK progress 3/10  ", 3, 10)]
    public void TryReportApplyProgress_ParsesMilestones(string line, int current, int total)
    {
        SyncProgressUpdate? seen = null;
        var progress = new ImmediateProgress<SyncProgressUpdate>(u => seen = u);

        PowerShellDnsPolicyProvider.TryReportApplyProgress(progress, "windc04", 200, line);

        Assert.NotNull(seen);
        Assert.Equal(SyncProgressPhase.ApplyProgress, seen!.Phase);
        Assert.Equal("windc04", seen.Target);
        Assert.Equal(current, seen.Current);
        Assert.Equal(total, seen.Total);
    }

    [Fact]
    public void TryReportApplyProgress_IgnoresNoise()
    {
        var hit = false;
        var progress = new ImmediateProgress<SyncProgressUpdate>(_ => hit = true);
        PowerShellDnsPolicyProvider.TryReportApplyProgress(progress, "x", 10, "OK added=10");
        PowerShellDnsPolicyProvider.TryReportApplyProgress(progress, "x", 10, null!);
        Assert.False(hit);
    }

    [Fact]
    public void ShortUrl_KeepsHostAndLeaf()
    {
        Assert.Equal(
            "abp.oisd.nl/domainswild_big.txt",
            SyncProgressUpdate.ShortUrl("https://abp.oisd.nl/domainswild_big.txt"));
    }

    [Fact]
    public async Task RunAsync_ReportsPhaseProgress()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://lol.test/blocklists.txt")
            .Respond("text/plain", "https://lists.test/a.txt\n");
        mockHttp.When("https://lol.test/whitelists.txt")
            .Respond("text/plain", "# none\n");
        mockHttp.When("https://lists.test/a.txt")
            .Respond("text/plain", "ads.one.com\nads.two.com\n");

        var phases = new List<SyncProgressPhase>();
        var progress = new ImmediateProgress<SyncProgressUpdate>(u => phases.Add(u.Phase));

        var dbPath = Path.Combine(Path.GetTempPath(), "dnsblocker-progress-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var state = new StateStore(dbPath);
            var engine = new SyncEngine(
                new BlocklistIngestor(mockHttp.ToHttpClient()),
                new MockDnsPolicyProvider(),
                state);

            var result = await engine.RunAsync(
                new AppConfig
                {
                    BlocklistsUrl = "https://lol.test/blocklists.txt",
                    WhitelistsUrl = "https://lol.test/whitelists.txt",
                    DnsTargets = ["localhost"],
                    CuratedYoutubeAds = false,
                    CuratedOisdBig = false,
                    CuratedOisdNsfw = false,
                    CuratedChadmayfieldPorn = false,
                    MaxDomains = 400_000
                },
                progress: progress);

            Assert.True(result.Success);
            Assert.Contains(SyncProgressPhase.Starting, phases);
            Assert.Contains(SyncProgressPhase.DownloadLol, phases);
            Assert.Contains(SyncProgressPhase.DownloadSource, phases);
            Assert.Contains(SyncProgressPhase.MergeWhitelist, phases);
            Assert.Contains(SyncProgressPhase.StoreSqlite, phases);
            Assert.Contains(SyncProgressPhase.ChunkPolicies, phases);
            Assert.Contains(SyncProgressPhase.ApplyTarget, phases);
            Assert.Contains(SyncProgressPhase.ApplyProgress, phases);
            Assert.Contains(SyncProgressPhase.ApplyDone, phases);
            Assert.Null(engine.CurrentProgress);
            Assert.False(engine.IsSyncing);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    /// <summary><see cref="Progress{T}"/> may post asynchronously; tests need synchronous capture.</summary>
    private sealed class ImmediateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public ImmediateProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
