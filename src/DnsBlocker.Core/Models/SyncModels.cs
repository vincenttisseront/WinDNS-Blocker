namespace DnsBlocker.Core.Models;

public enum TargetSyncStatus
{
    Idle,
    InProgress,
    Succeeded,
    Failed
}

public sealed class TargetSyncResult
{
    public required string Target { get; init; }
    public TargetSyncStatus Status { get; set; } = TargetSyncStatus.Idle;
    public string? Error { get; set; }
    public int PolicyCount { get; set; }
    public int DomainCount { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public TimeSpan? Duration { get; set; }
}

public sealed class SyncRunResult
{
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int DomainCount { get; set; }
    public int WhitelistCount { get; set; }
    public int SourceListCount { get; set; }
    public List<TargetSyncResult> Targets { get; init; } = [];
    public string? Error { get; set; }
    public bool Success => Error is null && Targets.Count > 0 && Targets.All(t => t.Status == TargetSyncStatus.Succeeded);
    public bool PartialSuccess => Targets.Any(t => t.Status == TargetSyncStatus.Succeeded) && Targets.Any(t => t.Status == TargetSyncStatus.Failed);
}

public sealed class ServiceStatus
{
    public bool IsSyncing { get; set; }
    public SyncRunResult? LastRun { get; set; }
    public DateTimeOffset? NextScheduledUtc { get; set; }
    public int ActiveDomainCount { get; set; }
    public List<string> RecentLogs { get; set; } = [];
    public bool StatsEnabled { get; set; }
    public string? StatsMessage { get; set; }
}

public sealed class StatsSnapshot
{
    public DateTimeOffset FromUtc { get; init; }
    public DateTimeOffset ToUtc { get; init; }
    public long TotalQueries { get; init; }
    public long BlockedQueries { get; init; }
    public IReadOnlyList<StatRow> TopBlockedDomains { get; init; } = [];
    public IReadOnlyList<StatRow> TopClients { get; init; } = [];
    public string? Message { get; init; }
}

public sealed class StatRow
{
    public required string Key { get; init; }
    public long Count { get; init; }
}
