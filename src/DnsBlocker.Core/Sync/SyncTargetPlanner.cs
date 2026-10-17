namespace DnsBlocker.Core.Sync;

/// <summary>
/// Why a DNS target was ordered for this sync pass (empty-first, then incremental refresh).
/// </summary>
public enum SyncTargetPriorityKind
{
    /// <summary>Live QRP inventory is empty — needs blocking coverage ASAP.</summary>
    Empty,

    /// <summary>Probe failed; treat like empty so we still try to push coverage.</summary>
    Unknown,

    /// <summary>Already has policies — refresh au fil de l'eau (not every target every sync).</summary>
    Populated
}

/// <summary>One row of the per-sync apply plan.</summary>
public sealed class SyncTargetPlanEntry
{
    public required string Target { get; init; }
    public SyncTargetPriorityKind Kind { get; init; }
    public int LivePolicyCount { get; init; }
    public bool ApplyThisRun { get; init; }

    /// <summary>English reason for FileLogBuffer / progress Detail.</summary>
    public required string ReasonEn { get; init; }
}

/// <summary>
/// Snapshot used to order farm targets: live inventory + last successful push time.
/// </summary>
public readonly record struct SyncTargetClassification(
    string Target,
    int LivePolicyCount,
    bool ProbeOk,
    DateTimeOffset? LastSucceededAt);

/// <summary>
/// Empty / unknown targets first (full apply this run); populated targets refreshed
/// incrementally (default one per sync, oldest success first) so dozens of DNS servers
/// do not turn every ApplyNow into a multi-hour fan-out.
/// </summary>
public static class SyncTargetPlanner
{
    /// <param name="maxPopulatedPerSync">
    /// Cap on already-populated targets to refresh this run. &lt;= 0 means unlimited (legacy full fan-out).
    /// </param>
    public static IReadOnlyList<SyncTargetPlanEntry> BuildPlan(
        IReadOnlyList<SyncTargetClassification> classified,
        int maxPopulatedPerSync)
    {
        if (classified.Count == 0)
            return [];

        var emptyOrUnknown = classified
            .Where(c => !c.ProbeOk || c.LivePolicyCount <= 0)
            .OrderBy(c => c.Target, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var populated = classified
            .Where(c => c.ProbeOk && c.LivePolicyCount > 0)
            .OrderBy(c => c.LastSucceededAt ?? DateTimeOffset.MinValue)
            .ThenBy(c => c.Target, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unlimited = maxPopulatedPerSync <= 0;
        var populatedBudget = unlimited ? populated.Count : Math.Max(0, maxPopulatedPerSync);
        var populatedApply = populated.Take(populatedBudget).ToList();
        var populatedDefer = populated.Skip(populatedBudget).ToList();

        var plan = new List<SyncTargetPlanEntry>(classified.Count);

        foreach (var c in emptyOrUnknown)
        {
            var kind = c.ProbeOk ? SyncTargetPriorityKind.Empty : SyncTargetPriorityKind.Unknown;
            var reason = kind == SyncTargetPriorityKind.Empty
                ? $"priority: empty install (live policies={c.LivePolicyCount})"
                : "priority: inventory probe failed — treating as empty";
            plan.Add(new SyncTargetPlanEntry
            {
                Target = c.Target,
                Kind = kind,
                LivePolicyCount = c.LivePolicyCount,
                ApplyThisRun = true,
                ReasonEn = reason
            });
        }

        foreach (var c in populatedApply)
        {
            plan.Add(new SyncTargetPlanEntry
            {
                Target = c.Target,
                Kind = SyncTargetPriorityKind.Populated,
                LivePolicyCount = c.LivePolicyCount,
                ApplyThisRun = true,
                ReasonEn = unlimited
                    ? $"incremental: populated refresh (live={c.LivePolicyCount}, full fan-out)"
                    : $"incremental: populated refresh (live={c.LivePolicyCount}, oldest first)"
            });
        }

        foreach (var c in populatedDefer)
        {
            plan.Add(new SyncTargetPlanEntry
            {
                Target = c.Target,
                Kind = SyncTargetPriorityKind.Populated,
                LivePolicyCount = c.LivePolicyCount,
                ApplyThisRun = false,
                ReasonEn =
                    $"deferred: already has {c.LivePolicyCount} policies — refresh next sync (au fil de l'eau)"
            });
        }

        return plan;
    }

    public static string Summarize(IReadOnlyList<SyncTargetPlanEntry> plan)
    {
        var empty = plan.Count(p => p.Kind is SyncTargetPriorityKind.Empty or SyncTargetPriorityKind.Unknown
                                    && p.ApplyThisRun);
        var populatedApply = plan.Count(p => p.Kind == SyncTargetPriorityKind.Populated && p.ApplyThisRun);
        var deferred = plan.Count(p => !p.ApplyThisRun);
        return
            $"Sync: priority plan — {empty} empty/unknown first, " +
            $"{populatedApply} populated this run, {deferred} deferred";
    }
}
