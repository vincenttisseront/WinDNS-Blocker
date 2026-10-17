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
    /// Parses hosts-format, plain domain, OISD domainswild (*.apex), or ABP (||apex^) lists
    /// into normalized FQDNs (no trailing dot). Leading <c>*.</c> is preserved for Windows DNS QRP.
    /// </summary>
    public static IReadOnlyCollection<string> ParseDomains(string content)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(content))
            return domains;
        using var reader = new StringReader(content);
        ParseDomainsInto(reader, domains);
        return domains;
    }

    /// <summary>
    /// Stream parse into an existing set — avoids holding the full download string + a second HashSet.
    /// When <paramref name="maxDomains"/> &gt; 0, stops once the set reaches that size (DC RSS cap).
    /// </summary>
    /// <returns>True if the cap was hit and remaining lines were skipped.</returns>
    public static bool ParseDomainsInto(TextReader reader, HashSet<string> domains, int maxDomains = 0)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(domains);

        var capped = false;
        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            if (maxDomains > 0 && domains.Count >= maxDomains)
            {
                capped = true;
                break;
            }

            // Normalize NBSP / odd whitespace so "0.0.0.0\u00a0host" still splits as hosts-file.
            var line = raw.Replace('\u00a0', ' ').Replace('\t', ' ').Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
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

            // Adblock Plus / uBlock: ||example.com^ or ||example.com^$third-party
            if (candidate.StartsWith("||", StringComparison.Ordinal))
            {
                candidate = candidate[2..];
                var caret = candidate.IndexOf('^');
                if (caret >= 0)
                    candidate = candidate[..caret];
            }

            var wildcard = false;
            if (candidate.StartsWith("*.", StringComparison.Ordinal))
            {
                wildcard = true;
                candidate = candidate[2..];
            }

            // Glued hosts sinkholes: "0.0.0.0.evil.com" / "127.0.0.1.evil.com" (no whitespace).
            candidate = StripLeadingHostsSinkholePrefix(candidate);

            if (TryNormalizeDomain(candidate, out var domain))
                domains.Add(wildcard ? "*." + domain : domain);
        }

        return capped;
    }

    /// <summary>
    /// Trims <paramref name="domains"/> down to <paramref name="maxDomains"/> (stable ordinal order).
    /// No-op when maxDomains ≤ 0 or already within budget.
    /// </summary>
    public static int EnforceMaxDomains(HashSet<string> domains, int maxDomains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        if (maxDomains <= 0 || domains.Count <= maxDomains)
            return 0;

        var keep = domains
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Take(maxDomains)
            .ToList();
        domains.Clear();
        foreach (var d in keep)
            domains.Add(d);
        domains.TrimExcess();
        return keep.Count;
    }

    /// <summary>
    /// Strips leading hosts-file sinkhole IPs glued onto hostnames
    /// (Vincent windc04: Criteria <c>EQ,0.0.0.0.creative.hpyrdr.com.</c> never matched queries).
    /// </summary>
    public static string StripLeadingHostsSinkholePrefix(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return candidate;

        var value = candidate.Trim();
        // Repeat: some broken lists nest prefixes.
        for (var i = 0; i < 4; i++)
        {
            var next = value;
            if (next.StartsWith("0.0.0.0.", StringComparison.OrdinalIgnoreCase))
                next = next["0.0.0.0.".Length..];
            else if (next.StartsWith("127.0.0.1.", StringComparison.OrdinalIgnoreCase))
                next = next["127.0.0.1.".Length..];
            else if (next.StartsWith("::1.", StringComparison.OrdinalIgnoreCase))
                next = next["::1.".Length..];
            else if (next.StartsWith("0.0.0.0", StringComparison.OrdinalIgnoreCase)
                     && next.Length > "0.0.0.0".Length
                     && next["0.0.0.0".Length] is not '.'
                     && !char.IsDigit(next["0.0.0.0".Length]))
                next = next["0.0.0.0".Length..];
            else if (next.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                     && next.Length > "127.0.0.1".Length
                     && next["127.0.0.1".Length] is not '.'
                     && !char.IsDigit(next["127.0.0.1".Length]))
                next = next["127.0.0.1".Length..];
            // Hosts shorthand "0.hostname.tld" (single sinkhole label) — not a real public name shape we want in QRP.
            else if (next.StartsWith("0.", StringComparison.OrdinalIgnoreCase)
                     && next.Length > 2
                     && next.IndexOf('.', 2) > 2)
                next = next[2..];
            else
                break;

            next = next.Trim().TrimStart('.');
            if (next.Length == 0 || string.Equals(next, value, StringComparison.Ordinal))
                break;
            value = next;
        }

        return value;
    }

    public static bool TryNormalizeDomain(string input, out string domain)
    {
        domain = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var value = StripLeadingHostsSinkholePrefix(input)
            .Trim()
            .Trim('.')
            .ToLowerInvariant();

        // Collapse accidental trailing/leading dots after strip.
        while (value.Contains("..", StringComparison.Ordinal))
            value = value.Replace("..", ".", StringComparison.Ordinal);
        value = value.Trim('.');

        if (value.Length == 0)
            return false;

        if (value.StartsWith("http://", StringComparison.Ordinal) || value.StartsWith("https://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                return false;
            value = uri.Host.Trim('.').ToLowerInvariant();
        }

        if (IgnoredHosts.Contains(value) || LooksLikeIp(value))
            return false;

        // Reject pure numeric-label IPv4-shaped leftovers (e.g. 0.0.0.0).
        if (IsNumericLabelHost(value))
            return false;

        if (!DomainRegex().IsMatch(value))
            return false;

        domain = value;
        return true;
    }

    /// <summary>True when every DNS label is digits only (IPv4-like / junk), e.g. 0.0.0.0.</summary>
    private static bool IsNumericLabelHost(string value)
    {
        var labels = value.Split('.');
        if (labels.Length < 2)
            return false;
        foreach (var label in labels)
        {
            if (label.Length == 0)
                return true;
            foreach (var ch in label)
            {
                if (!char.IsAsciiDigit(ch))
                    return false;
            }
        }

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
    /// Packs domains into FQDN criterion chunks under <paramref name="maxFqdnCriterionChars"/>
    /// (including leading <c>EQ,</c> and commas). Oversized criteria are a known cause of
    /// policies that Add successfully but store empty/invalid FQDN criteria and never block.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ChunkByFqdnBudget(
        IEnumerable<string> domains,
        int maxFqdnCriterionChars,
        int maxDomainsPerChunk = 200)
    {
        if (maxFqdnCriterionChars < 32)
            throw new ArgumentOutOfRangeException(nameof(maxFqdnCriterionChars));
        if (maxDomainsPerChunk < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDomainsPerChunk));

        // One sorted array (not Distinct+OrderBy+ToList pipeline copies). HashSets are already unique.
        string[] ordered;
        if (domains is HashSet<string> set)
        {
            ordered = new string[set.Count];
            var i = 0;
            foreach (var d in set)
            {
                if (string.IsNullOrWhiteSpace(d) || d.Contains(',', StringComparison.Ordinal))
                    continue;
                ordered[i++] = d;
            }

            if (i < ordered.Length)
                Array.Resize(ref ordered, i);
            Array.Sort(ordered, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            ordered = domains
                .Where(d => !string.IsNullOrWhiteSpace(d) && !d.Contains(',', StringComparison.Ordinal))
                .Select(d => d.Trim().Trim('.').ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var chunks = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        var used = 3; // "EQ,"

        foreach (var domain in ordered)
        {
            var extra = (current.Count == 0 ? 0 : 1) + domain.Length;
            if (current.Count >= maxDomainsPerChunk
                || (current.Count > 0 && used + extra > maxFqdnCriterionChars))
            {
                chunks.Add(current);
                current = [];
                used = 3;
                extra = domain.Length;
            }

            if (3 + domain.Length > maxFqdnCriterionChars)
            {
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

    /// <summary>
    /// Windows DNS QRP <c>EQ,*.beeg.com</c> may not match the apex <c>beeg.com</c> on all
    /// builds. For every <c>*.apex</c> entry, also include the bare apex so nslookup of the
    /// registrable name is covered.
    /// </summary>
    public static HashSet<string> EnsureApexForWildcards(IEnumerable<string> domains)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in domains)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var domain = raw.Trim().Trim('.').ToLowerInvariant();
            result.Add(domain);
            if (domain.StartsWith("*.", StringComparison.Ordinal) && domain.Length > 2)
            {
                var apex = domain[2..];
                if (TryNormalizeDomain(apex, out var normalizedApex))
                    result.Add(normalizedApex);
            }
        }

        return result;
    }

    /// <summary>Adds bare apex for every <c>*.apex</c> already in <paramref name="domains"/> (no second set).</summary>
    public static void EnsureApexForWildcardsInPlace(HashSet<string> domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        List<string>? toAdd = null;
        foreach (var domain in domains)
        {
            if (!domain.StartsWith("*.", StringComparison.Ordinal) || domain.Length <= 2)
                continue;
            var apex = domain[2..];
            if (!TryNormalizeDomain(apex, out var normalizedApex))
                continue;
            if (domains.Contains(normalizedApex))
                continue;
            toAdd ??= [];
            toAdd.Add(normalizedApex);
        }

        if (toAdd is null)
            return;
        foreach (var a in toAdd)
            domains.Add(a);
    }

    /// <summary>
    /// Drops exact hostnames already covered by a <c>*.apex</c> wildcard (keeps the wildcard
    /// and the bare apex). Shrinks multi-million host+OISD merges so QRP policy count stays
    /// practical without losing coverage.
    /// </summary>
    public static HashSet<string> CompactCoveredByWildcards(IEnumerable<string> domains)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in domains)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            set.Add(raw.Trim().Trim('.').ToLowerInvariant());
        }

        CompactCoveredByWildcardsInPlace(set);
        return set;
    }

    /// <summary>In-place compact — mutates <paramref name="set"/> (no full copy).</summary>
    public static void CompactCoveredByWildcardsInPlace(HashSet<string> set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var wildApexes = set
            .Where(d => d.StartsWith("*.", StringComparison.Ordinal) && d.Length > 2)
            .Select(d => d[2..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(a => a.Length)
            .ToList();

        if (wildApexes.Count == 0)
            return;

        var remove = new List<string>();
        foreach (var domain in set)
        {
            if (domain.StartsWith("*.", StringComparison.Ordinal))
                continue;

            foreach (var apex in wildApexes)
            {
                // Keep bare apex (insurance); drop subdomain hosts covered by *.apex.
                if (domain.EndsWith("." + apex, StringComparison.OrdinalIgnoreCase))
                {
                    remove.Add(domain);
                    break;
                }
            }
        }

        foreach (var d in remove)
            set.Remove(d);
    }

    /// <summary>
    /// Apex expansion + wildcard compact on one set (avoids holding ingest + expanded + compact copies).
    /// </summary>
    public static void PrepareForQrpInPlace(HashSet<string> domains)
    {
        EnsureApexForWildcardsInPlace(domains);
        CompactCoveredByWildcardsInPlace(domains);
    }

    public static HashSet<string> ApplyWhitelist(IEnumerable<string> blocked, IEnumerable<string> whitelist)
    {
        var result = blocked is HashSet<string> hs
            ? new HashSet<string>(hs, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);
        ApplyWhitelistInPlace(result, whitelist);
        return result;
    }

    /// <summary>Removes whitelist hits from <paramref name="blocked"/> in place (no third giant set).</summary>
    public static void ApplyWhitelistInPlace(HashSet<string> blocked, IEnumerable<string> whitelist)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        var wl = whitelist as HashSet<string> ?? new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase);
        if (wl.Count == 0)
            return;

        var remove = new List<string>();
        foreach (var domain in blocked)
        {
            if (wl.Contains(domain))
            {
                remove.Add(domain);
                continue;
            }

            foreach (var allowed in wl)
            {
                if (allowed.StartsWith("*.", StringComparison.Ordinal) &&
                    (domain.Equals(allowed[2..], StringComparison.OrdinalIgnoreCase)
                     || domain.EndsWith("." + allowed[2..], StringComparison.OrdinalIgnoreCase)))
                {
                    remove.Add(domain);
                    break;
                }
            }
        }

        foreach (var d in remove)
            blocked.Remove(d);
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
