using DnsBlocker.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnsBlocker.Core.Ingest;

public sealed class BlocklistIngestor
{
    private readonly HttpClient _http;
    private readonly ILogger<BlocklistIngestor> _logger;

    public BlocklistIngestor(HttpClient? httpClient = null, ILogger<BlocklistIngestor>? logger = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DnsBlocker/1.0");
        _logger = logger ?? NullLogger<BlocklistIngestor>.Instance;
    }

    public async Task<IngestResult> IngestAsync(
        string blocklistsUrl,
        string whitelistsUrl,
        IEnumerable<string>? extraBlocklistUrls = null,
        IEnumerable<string>? extraWhitelistUrls = null,
        CancellationToken cancellationToken = default,
        int maxDomains = 0)
    {
        var blocklistUrls = new List<string>();
        var whitelistUrls = new List<string>();

        if (!string.IsNullOrWhiteSpace(blocklistsUrl))
        {
            var lolBlock = await DownloadTextAsync(blocklistsUrl.Trim(), cancellationToken).ConfigureAwait(false);
            blocklistUrls.AddRange(DomainListParser.ParseListOfLists(lolBlock));
        }

        if (!string.IsNullOrWhiteSpace(whitelistsUrl))
        {
            var lolWhite = await DownloadTextAsync(whitelistsUrl.Trim(), cancellationToken).ConfigureAwait(false);
            whitelistUrls.AddRange(DomainListParser.ParseListOfLists(lolWhite));
        }

        if (extraBlocklistUrls is not null)
            blocklistUrls.AddRange(extraBlocklistUrls.Where(u => !string.IsNullOrWhiteSpace(u)));
        if (extraWhitelistUrls is not null)
            whitelistUrls.AddRange(extraWhitelistUrls.Where(u => !string.IsNullOrWhiteSpace(u)));

        blocklistUrls = blocklistUrls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        whitelistUrls = whitelistUrls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        _logger.LogInformation(
            "Resolved {BlockCount} blocklist URLs and {WhiteCount} whitelist URLs (maxDomains={Max}, RSS {Rss})",
            blocklistUrls.Count,
            whitelistUrls.Count,
            maxDomains <= 0 ? "unlimited" : maxDomains.ToString(),
            ProcessMemory.FormatWorkingSet());

        // Single working set for blocked domains — stream each source (no full-file string + HashSet pair).
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var truncated = false;

        foreach (var url in blocklistUrls)
        {
            if (maxDomains > 0 && blocked.Count >= maxDomains)
            {
                truncated = true;
                _logger.LogWarning(
                    "MaxDomains={Max} reached — skipping remaining blocklist sources (RSS {Rss})",
                    maxDomains, ProcessMemory.FormatWorkingSet());
                break;
            }

            try
            {
                var before = blocked.Count;
                var hitCap = await DownloadDomainsIntoAsync(url, blocked, maxDomains, cancellationToken)
                    .ConfigureAwait(false);
                if (hitCap)
                    truncated = true;
                _logger.LogInformation(
                    "Blocklist {Url}: +{Added} (total {Total}, RSS {Rss})",
                    url, blocked.Count - before, blocked.Count, ProcessMemory.FormatWorkingSet());
                // Brief yield so DNS/AD share the core during multi-MB parse on a DC.
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = $"Failed blocklist {url}: {ex.Message}";
                _logger.LogWarning(ex, "Failed to download blocklist {Url}", url);
                errors.Add(msg);
            }
        }

        foreach (var url in whitelistUrls)
        {
            try
            {
                await DownloadDomainsIntoAsync(url, allowed, maxDomains: 0, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var msg = $"Failed whitelist {url}: {ex.Message}";
                _logger.LogWarning(ex, "Failed to download whitelist {Url}", url);
                errors.Add(msg);
            }
        }

        var wlCount = allowed.Count;
        DomainListParser.ApplyWhitelistInPlace(blocked, allowed);
        allowed.Clear();
        allowed.TrimExcess();
        ProcessMemory.TrimAfterLargeDrop();

        if (maxDomains > 0 && blocked.Count > maxDomains)
        {
            DomainListParser.EnforceMaxDomains(blocked, maxDomains);
            truncated = true;
        }

        if (truncated)
        {
            errors.Add(
                $"MaxDomains={maxDomains} truncated the block set to {blocked.Count} entries. " +
                "Prefer curated OISD wildcards (empty blocklists.txt URL) on domain controllers.");
            _logger.LogWarning(
                "Ingest truncated to MaxDomains={Max} ({Count} kept, RSS {Rss})",
                maxDomains, blocked.Count, ProcessMemory.FormatWorkingSet());
        }

        _logger.LogInformation(
            "Ingest complete: {Domains} domains after whitelist (wl sources had {WlCount}, RSS {Rss})",
            blocked.Count, wlCount, ProcessMemory.FormatWorkingSet());

        return new IngestResult
        {
            Domains = blocked,
            WhitelistCount = wlCount,
            SourceListCount = blocklistUrls.Count,
            Errors = errors,
            TruncatedByMaxDomains = truncated
        };
    }

    private async Task<bool> DownloadDomainsIntoAsync(
        string url,
        HashSet<string> domains,
        int maxDomains,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Downloading {Url}", url);
        using var response = await _http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        return DomainListParser.ParseDomainsInto(reader, domains, maxDomains);
    }

    private async Task<string> DownloadTextAsync(string url, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Downloading {Url}", url);
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class IngestResult
{
    public required HashSet<string> Domains { get; init; }
    public int WhitelistCount { get; init; }
    public int SourceListCount { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool TruncatedByMaxDomains { get; init; }
}
