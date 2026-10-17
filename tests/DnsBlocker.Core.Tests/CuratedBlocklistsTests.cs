using DnsBlocker.Core.Ingest;
using DnsBlocker.Core.Models;

namespace DnsBlocker.Core.Tests;

public class CuratedBlocklistsTests
{
    [Fact]
    public void ResolveExtraBlocklistUrls_Defaults_IncludeYoutubeOisdBigNsfw_NotAdult()
    {
        var urls = CuratedBlocklists.ResolveExtraBlocklistUrls(new AppConfig());
        Assert.Contains(CuratedBlocklists.YoutubeAds, urls);
        Assert.Contains(CuratedBlocklists.OisdBig, urls);
        Assert.Contains(CuratedBlocklists.OisdNsfw, urls);
        Assert.DoesNotContain(CuratedBlocklists.ChadmayfieldPornAll, urls);
        Assert.Equal(3, urls.Count);
    }

    [Fact]
    public void ResolveExtraBlocklistUrls_MergesCustomAndOptInAdult()
    {
        var cfg = new AppConfig
        {
            CuratedYoutubeAds = false,
            CuratedOisdBig = false,
            CuratedOisdNsfw = true,
            CuratedChadmayfieldPorn = true,
            ExtraBlocklistUrls = ["https://example.com/custom.txt", CuratedBlocklists.OisdNsfw]
        };

        var urls = CuratedBlocklists.ResolveExtraBlocklistUrls(cfg);
        Assert.Equal(3, urls.Count);
        Assert.Contains(CuratedBlocklists.OisdNsfw, urls);
        Assert.Contains(CuratedBlocklists.ChadmayfieldPornAll, urls);
        Assert.Contains("https://example.com/custom.txt", urls);
    }
}
