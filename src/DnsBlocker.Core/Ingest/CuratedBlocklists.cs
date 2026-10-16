using DnsBlocker.Core.Models;

namespace DnsBlocker.Core.Ingest;

/// <summary>
/// First-class extra blocklist sources merged into <see cref="AppConfig.ExtraBlocklistUrls"/>
/// on every scheduled sync / ApplyNow.
/// </summary>
public static class CuratedBlocklists
{
    /// <summary>
    /// kboghdady YouTube ads for Pi-hole — plain domains (canonical raw list).
    /// </summary>
    public const string YoutubeAds =
        "https://raw.githubusercontent.com/kboghdady/youTube_ads_4_pi-hole/master/youtubelist.txt";

    /// <summary>
    /// OISD "big" — domainswild (*.apex) so Windows DNS QRP EQ,*.domain covers subdomains.
    /// Chosen over https://big.oisd.nl/ (ABP) and /domainswild2 (apex-only) for QRP fit.
    /// </summary>
    public const string OisdBig =
        "https://big.oisd.nl/domainswild";

    /// <summary>
    /// OISD NSFW — family-oriented, lower false-positive adult/NSFW list (domainswild).
    /// </summary>
    public const string OisdNsfw =
        "https://nsfw.oisd.nl/domainswild";

    /// <summary>
    /// Chadmayfield porn blocklist (full). Large (~1.9M); opt-in only.
    /// </summary>
    public const string ChadmayfieldPornAll =
        "https://raw.githubusercontent.com/chadmayfield/my-pihole-blocklists/master/lists/pi_blocklist_porn_all.list";

    /// <summary>
    /// Builds the effective extra blocklist URL set from curated toggles + custom URLs.
    /// </summary>
    public static IReadOnlyList<string> ResolveExtraBlocklistUrls(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var urls = new List<string>();
        if (config.CuratedYoutubeAds)
            urls.Add(YoutubeAds);
        if (config.CuratedOisdBig)
            urls.Add(OisdBig);
        if (config.CuratedOisdNsfw)
            urls.Add(OisdNsfw);
        if (config.CuratedChadmayfieldPorn)
            urls.Add(ChadmayfieldPornAll);

        if (config.ExtraBlocklistUrls is { Count: > 0 })
        {
            foreach (var url in config.ExtraBlocklistUrls)
            {
                if (!string.IsNullOrWhiteSpace(url))
                    urls.Add(url.Trim());
            }
        }

        return urls
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
