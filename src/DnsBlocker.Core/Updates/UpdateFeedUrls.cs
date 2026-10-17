namespace DnsBlocker.Core.Updates;

/// <summary>
/// Stable public update feed for the GitHub-hosted WinDNS Blocker releases.
/// Prefer <see cref="DefaultPublicFeedUrl"/> (<c>…/releases/latest/download/version.json</c>)
/// so clients always follow the newest release asset without retargeting the URL.
/// </summary>
public static class UpdateFeedUrls
{
    public const string GitHubOwner = "vincenttisseront";
    public const string GitHubRepo = "WinDNS-Blocker";

    /// <summary>
    /// Stable GitHub Releases "latest" asset URL for <c>version.json</c>.
    /// </summary>
    public const string DefaultPublicFeedUrl =
        "https://github.com/vincenttisseront/WinDNS-Blocker/releases/latest/download/version.json";

    /// <summary>
    /// Normalizes common mistaken GitHub feed URLs (e.g. <c>…/releases/download/latest/…</c>
    /// which 404s) to the working <c>…/releases/latest/download/…</c> form.
    /// </summary>
    public static string Resolve(string? feedUrl)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
            return "";

        var url = feedUrl.Trim();

        // Wrong: /releases/download/latest/version.json  → 404
        // Right: /releases/latest/download/version.json
        const string badLatest =
            "github.com/vincenttisseront/WinDNS-Blocker/releases/download/latest/";
        const string goodLatest =
            "github.com/vincenttisseront/WinDNS-Blocker/releases/latest/download/";

        var idx = url.IndexOf(badLatest, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var prefix = url[..idx];
            var suffix = url[(idx + badLatest.Length)..];
            url = prefix + goodLatest + suffix;
        }

        return url;
    }

    public static string TagFeedUrl(string version)
    {
        var v = version.Trim().TrimStart('v', 'V');
        return $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/download/v{v}/version.json";
    }

    public static string TagMsiUrl(string version)
    {
        var v = version.Trim().TrimStart('v', 'V');
        return $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/download/v{v}/DnsBlocker-{v}.msi";
    }

    public static string TagReleaseBaseUrl(string version)
    {
        var v = version.Trim().TrimStart('v', 'V');
        return $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/download/v{v}";
    }
}
