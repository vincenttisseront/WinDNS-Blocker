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
        CancellationToken cancellationToken = default)
    {
        var blocklistUrls = new List<string>();
        var whitelistUrls = new List<string>();

        var lolBlock = await DownloadTextAsync(blocklistsUrl, cancellationToken).ConfigureAwait(false);
        blocklistUrls.AddRange(DomainListParser.ParseListOfLists(lolBlock));

        var lolWhite = await DownloadTextAsync(whitelistsUrl, cancellationToken).ConfigureAwait(false);
        whitelistUrls.AddRange(DomainListParser.ParseListOfLists(lolWhite));

        if (extraBlocklistUrls is not null)
            blocklistUrls.AddRange(extraBlocklistUrls.Where(u => !string.IsNullOrWhiteSpace(u)));
        if (extraWhitelistUrls is not null)
            whitelistUrls.AddRange(extraWhitelistUrls.Where(u => !string.IsNullOrWhiteSpace(u)));

        blocklistUrls = blocklistUrls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        whitelistUrls = whitelistUrls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        _logger.LogInformation("Resolved {BlockCount} blocklist URLs and {WhiteCount} whitelist URLs",
            blocklistUrls.Count, whitelistUrls.Count);

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var url in blocklistUrls)
        {
            try
            {
                var content = await DownloadTextAsync(url, cancellationToken).ConfigureAwait(false);
                foreach (var d in DomainListParser.ParseDomains(content))
                    blocked.Add(d);
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
                var content = await DownloadTextAsync(url, cancellationToken).ConfigureAwait(false);
                foreach (var d in DomainListParser.ParseDomains(content))
                    allowed.Add(d);
            }
            catch (Exception ex)
            {
                var msg = $"Failed whitelist {url}: {ex.Message}";
                _logger.LogWarning(ex, "Failed to download whitelist {Url}", url);
                errors.Add(msg);
            }
        }

        var final = DomainListParser.ApplyWhitelist(blocked, allowed);
        return new IngestResult
        {
            Domains = final,
            WhitelistCount = allowed.Count,
            SourceListCount = blocklistUrls.Count,
            Errors = errors
        };
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
}
