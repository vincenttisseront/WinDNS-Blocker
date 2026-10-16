using System.Text.RegularExpressions;

namespace DnsBlocker.Core.Ingest;

public static partial class DomainListParser
{
    private static readonly HashSet<string> IgnoredHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "localhost.localdomain",
        "broadcasthost",
        "local",
        "ip6-localhost",
        "ip6-loopback",
        "ip6-localnet",
        "ip6-mcastprefix",
        "ip6-allnodes",
        "ip6-allrouters",
        "0.0.0.0"
    };

    /// <summary>
    /// Extracts list-of-lists URLs (http/https) from a Steve Jenkins style index file.
    /// </summary>
    public static IReadOnlyList<string> ParseListOfLists(string content)
    {
        var urls = new List<string>();
        foreach (var raw in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // Allow trailing comments
            var hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash].Trim();

            if (Uri.TryCreate(line, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                urls.Add(uri.ToString());
            }
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Parses hosts-format or plain domain lists into normalized FQDNs (no trailing dot).
    /// </summary>
    public static IReadOnlyCollection<string> ParseDomains(string content)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash].Trim();

            if (line.Length == 0)
                continue;

            // hosts: "0.0.0.0 example.com" or "127.0.0.1 example.com alias"
            var parts = WhitespaceRegex().Split(line);
            string candidate;
            if (parts.Length >= 2 && LooksLikeIp(parts[0]))
                candidate = parts[1];
            else
                candidate = parts[0];

            if (TryNormalizeDomain(candidate, out var domain))
                domains.Add(domain);
        }

        return domains;
    }

    public static bool TryNormalizeDomain(string input, out string domain)
    {
        domain = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var value = input.Trim().Trim('.').ToLowerInvariant();
        if (value.StartsWith("http://", StringComparison.Ordinal) || value.StartsWith("https://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return false;
            value = uri.Host.Trim('.').ToLowerInvariant();
        }

        if (IgnoredHosts.Contains(value) || LooksLikeIp(value))
            return false;

        if (!DomainRegex().IsMatch(value))
            return false;

        domain = value;
        return true;
    }

    public static IReadOnlyList<IReadOnlyList<string>> Chunk(IEnumerable<string> domains, int chunkSize)
    {
        if (chunkSize < 1)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be >= 1.");

        return domains
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select((domain, index) => (domain, index))
            .GroupBy(x => x.index / chunkSize)
            .Select(g => (IReadOnlyList<string>)g.Select(x => x.domain).ToList())
            .ToList();
    }

    /// <summary>
    /// Packs domains into FQDN criterion chunks that stay under <paramref name="maxFqdnCriterionChars"/>
    /// including the leading "EQ," and separating commas (Windows CIM / QRP practical limit).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ChunkByFqdnBudget(
        IEnumerable<string> domains,
        int maxFqdnCriterionChars,
        int maxDomainsPerChunk = 250)
    {
        if (maxFqdnCriterionChars < 32)
            throw new ArgumentOutOfRangeException(nameof(maxFqdnCriterionChars));
        if (maxDomainsPerChunk < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDomainsPerChunk));

        var ordered = domains
            .Where(d => !string.IsNullOrWhiteSpace(d) && !d.Contains(',', StringComparison.Ordinal))
            .Select(d => d.Trim().Trim('.').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var chunks = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        // "EQ," prefix
        var used = 3;

        foreach (var domain in ordered)
        {
            var extra = (current.Count == 0 ? 0 : 1) + domain.Length; // comma + domain
            if (current.Count >= maxDomainsPerChunk || (current.Count > 0 && used + extra > maxFqdnCriterionChars))
            {
                chunks.Add(current);
                current = [];
                used = 3;
                extra = domain.Length;
            }

            if (3 + domain.Length > maxFqdnCriterionChars)
            {
                // Single domain exceeds budget — still emit alone so caller sees it rather than drop silently.
                if (current.Count > 0)
                {
                    chunks.Add(current);
                    current = [];
                }

                chunks.Add(new List<string> { domain });
                used = 3;
                continue;
            }

            current.Add(domain);
            used += extra;
        }

        if (current.Count > 0)
            chunks.Add(current);

        return chunks;
    }

    public static HashSet<string> ApplyWhitelist(IEnumerable<string> blocked, IEnumerable<string> whitelist)
    {
        var wl = new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in blocked)
        {
            if (wl.Contains(domain))
                continue;

            // Also skip if any whitelist entry is a parent suffix match for exact allow lists that use leading *.
            var skip = false;
            foreach (var allowed in wl)
            {
                if (allowed.StartsWith("*.", StringComparison.Ordinal) &&
                    (domain.Equals(allowed[2..], StringComparison.OrdinalIgnoreCase)
                     || domain.EndsWith("." + allowed[2..], StringComparison.OrdinalIgnoreCase)))
                {
                    skip = true;
                    break;
                }
            }

            if (!skip)
                result.Add(domain);
        }

        return result;
    }

    private static bool LooksLikeIp(string value)
    {
        if (System.Net.IPAddress.TryParse(value, out _))
            return true;
        return false;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(?=.{1,253}$)(?!-)[a-z0-9-]+(\.[a-z0-9-]+)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DomainRegex();
}
