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
    /// <summary>True when this target is the hub's local DNS Server (no WinRM).</summary>
    public bool IsLocal { get; set; }
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

    /// <summary>Last live QRP probe (Get-DnsServerQueryResolutionPolicy), if any.</summary>
    public TargetPolicyStatusResult? LastPolicyProbe { get; set; }
}

/// <summary>
/// Live QRP health for one DNS target (probe ≠ last sync push).
/// </summary>
public sealed class TargetPolicyStatus
{
    public required string Target { get; init; }
    /// <summary>True when this target is the hub's local DNS Server (no WinRM).</summary>
    public bool IsLocal { get; init; }
    public TargetSyncStatus LastSyncStatus { get; init; } = TargetSyncStatus.Idle;
    public DateTimeOffset? LastSyncFinishedAt { get; init; }
    public string? LastSyncError { get; init; }
    public int LastSyncPolicyCount { get; init; }
    public int LivePolicyCount { get; init; }
    public int LiveEnabledCount { get; init; }
    /// <summary>Policies whose persisted FQDN criterion text length is 0 (inert — will not match).</summary>
    public int LiveEmptyFqdnCount { get; init; }
    public IReadOnlyList<string> LivePolicyNames { get; init; } = [];
    public bool ProbeOk { get; init; }
    public string? ProbeError { get; init; }
    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>True when probe succeeded, all live policies are enabled with non-empty FQDN criteria, and counts match last sync.</summary>
    public bool AppliedOk =>
        ProbeOk
        && LivePolicyCount > 0
        && LiveEnabledCount == LivePolicyCount
        && LiveEmptyFqdnCount == 0
        && LastSyncStatus == TargetSyncStatus.Succeeded
        && LivePolicyCount == LastSyncPolicyCount;

    public string AppliedLabel =>
        !ProbeOk ? "Sonde en échec"
        : LivePolicyCount == 0 ? "Aucune politique"
        : LiveEmptyFqdnCount > 0 ? "Critères FQDN vides"
        : LiveEnabledCount < LivePolicyCount ? "Partiellement désactivées"
        : LastSyncStatus == TargetSyncStatus.Succeeded && LivePolicyCount == LastSyncPolicyCount ? "Appliquées"
        : "Écart sync / live";
}

public sealed class TargetPolicyStatusResult
{
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<TargetPolicyStatus> Targets { get; init; } = [];
    public string? Message { get; init; }
}

public sealed class StatsSnapshot
{
    public DateTimeOffset FromUtc { get; init; }
    public DateTimeOffset ToUtc { get; init; }
    public long TotalQueries { get; init; }
    public long BlockedQueries { get; init; }
    public int TopN { get; init; } = 20;
    public IReadOnlyList<StatRow> TopQueriedDomains { get; init; } = [];
    public IReadOnlyList<StatRow> TopBlockedDomains { get; init; } = [];
    public IReadOnlyList<StatRow> TopClients { get; init; } = [];
    public string? Message { get; init; }
}

public sealed class StatRow
{
    public required string Key { get; init; }
    public long Count { get; init; }
}
