namespace DnsBlocker.Core.Sync;

/// <summary>
/// Phase-level sync progress for the Status Action Log / Busy header (not per-domain spam).
/// <see cref="Message"/> is English (service FileLogBuffer); UI localizes via <see cref="Phase"/>.
/// </summary>
public enum SyncProgressPhase
{
    Starting,
    DownloadLol,
    DownloadSource,
    MergeWhitelist,
    StoreSqlite,
    ChunkPolicies,
    ApplyTarget,
    ApplyProgress,
    ApplyDone,
    VerifyProbe,
    BlockTest,
    Done,
    Error
}

public sealed class SyncProgressUpdate
{
    public SyncProgressPhase Phase { get; init; }

    /// <summary>English one-liner for FileLogBuffer / Logs tab.</summary>
    public required string Message { get; init; }

    public string? Url { get; init; }
    public string? Target { get; init; }
    public int Current { get; init; }
    public int Total { get; init; }
    public int Domains { get; init; }
    public int Added { get; init; }
    public bool Success { get; init; } = true;
    public string? Detail { get; init; }

    public static SyncProgressUpdate Starting(int targetCount) => new()
    {
        Phase = SyncProgressPhase.Starting,
        Message = $"Sync: starting for {targetCount} target(s)",
        Total = targetCount
    };

    public static SyncProgressUpdate DownloadLol(string kind, string url) => new()
    {
        Phase = SyncProgressPhase.DownloadLol,
        Message = $"Sync: downloading {kind} list-of-lists — {ShortUrl(url)}",
        Url = url,
        Detail = kind
    };

    public static SyncProgressUpdate DownloadSource(
        int index, int total, string url, int added, int domainTotal) => new()
    {
        Phase = SyncProgressPhase.DownloadSource,
        Message =
            $"Sync: download {index}/{total} {ShortUrl(url)} (+{added} → {domainTotal} domains)",
        Url = url,
        Current = index,
        Total = total,
        Added = added,
        Domains = domainTotal
    };

    public static SyncProgressUpdate Merge(int domains, int whitelistCount) => new()
    {
        Phase = SyncProgressPhase.MergeWhitelist,
        Message = $"Sync: merge/whitelist — {domains} domains (wl {whitelistCount})",
        Domains = domains,
        Total = whitelistCount
    };

    public static SyncProgressUpdate Store(int domains) => new()
    {
        Phase = SyncProgressPhase.StoreSqlite,
        Message = $"Sync: storing {domains} domains in SQLite",
        Domains = domains
    };

    public static SyncProgressUpdate Chunk(int domains, int policies) => new()
    {
        Phase = SyncProgressPhase.ChunkPolicies,
        Message = $"Sync: prepared {domains} domains → {policies} QRP policies",
        Domains = domains,
        Total = policies
    };

    public static SyncProgressUpdate ApplyTarget(string target, int policies) => new()
    {
        Phase = SyncProgressPhase.ApplyTarget,
        Message = $"Sync: applying QRP on {target} ({policies} policies)…",
        Target = target,
        Total = policies
    };

    public static SyncProgressUpdate ApplyProgress(string target, int current, int total) => new()
    {
        Phase = SyncProgressPhase.ApplyProgress,
        Message = $"Sync: {target} — policies {current}/{total}",
        Target = target,
        Current = current,
        Total = total
    };

    public static SyncProgressUpdate ApplyDone(string target, int policies, bool ok, string? error = null) =>
        new()
        {
            Phase = SyncProgressPhase.ApplyDone,
            Message = ok
                ? $"Sync: {target} — apply OK ({policies} policies)"
                : $"Sync: {target} — apply FAIL — {error}",
            Target = target,
            Total = policies,
            Success = ok,
            Detail = error
        };

    public static SyncProgressUpdate Verify() => new()
    {
        Phase = SyncProgressPhase.VerifyProbe,
        Message = "Sync: verifying QRP policies on targets…"
    };

    public static SyncProgressUpdate BlockTest(string domain, string target) => new()
    {
        Phase = SyncProgressPhase.BlockTest,
        Message = $"Sync: block test {domain} @ {target}…",
        Target = target,
        Url = domain
    };

    public static SyncProgressUpdate Done(bool success, int domains, int targets) => new()
    {
        Phase = SyncProgressPhase.Done,
        Message = success
            ? $"Sync: done — {domains} domains / {targets} target(s) OK"
            : $"Sync: finished with errors — {domains} domains / {targets} target(s)",
        Domains = domains,
        Total = targets,
        Success = success
    };

    public static SyncProgressUpdate Error(string error) => new()
    {
        Phase = SyncProgressPhase.Error,
        Message = $"Sync: error — {error}",
        Success = false,
        Detail = error
    };

    public static string ShortUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "(empty)";
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var leaf = uri.Segments.Length > 0
                    ? uri.Segments[^1].TrimEnd('/')
                    : "";
                if (string.IsNullOrEmpty(leaf) || leaf == "/")
                    return uri.Host;
                return uri.Host + "/" + leaf;
            }
        }
        catch
        {
            // fall through
        }

        return url.Length <= 64 ? url : url[..61] + "…";
    }
}
