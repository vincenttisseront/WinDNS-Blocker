using DnsBlocker.Core.Sync;

namespace DnsBlocker.Core.Tests;

public class SyncTargetPlannerTests
{
    [Fact]
    public void BuildPlan_EmptyFirst_ThenOnePopulated_DefersRest()
    {
        var classified = new List<SyncTargetClassification>
        {
            new("dns-old.local", LivePolicyCount: 50, ProbeOk: true,
                LastSucceededAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            new("dns-empty.local", LivePolicyCount: 0, ProbeOk: true, LastSucceededAt: null),
            new("dns-newer.local", LivePolicyCount: 50, ProbeOk: true,
                LastSucceededAt: DateTimeOffset.Parse("2026-06-01T00:00:00Z")),
            new("dns-unknown.local", LivePolicyCount: 0, ProbeOk: false, LastSucceededAt: null),
        };

        var plan = SyncTargetPlanner.BuildPlan(classified, maxPopulatedPerSync: 1);

        Assert.Equal(4, plan.Count);
        Assert.Equal(
            ["dns-empty.local", "dns-unknown.local", "dns-old.local", "dns-newer.local"],
            plan.Select(p => p.Target).ToArray());

        Assert.True(plan[0].ApplyThisRun);
        Assert.Equal(SyncTargetPriorityKind.Empty, plan[0].Kind);
        Assert.True(plan[1].ApplyThisRun);
        Assert.Equal(SyncTargetPriorityKind.Unknown, plan[1].Kind);
        Assert.True(plan[2].ApplyThisRun);
        Assert.Equal(SyncTargetPriorityKind.Populated, plan[2].Kind);
        Assert.Contains("oldest", plan[2].ReasonEn, StringComparison.OrdinalIgnoreCase);

        Assert.False(plan[3].ApplyThisRun);
        Assert.Contains("deferred", plan[3].ReasonEn, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPlan_ZeroBudget_AppliesAllPopulated()
    {
        var classified = new List<SyncTargetClassification>
        {
            new("a.local", 10, true, DateTimeOffset.UtcNow),
            new("b.local", 10, true, DateTimeOffset.UtcNow.AddDays(-1)),
        };

        var plan = SyncTargetPlanner.BuildPlan(classified, maxPopulatedPerSync: 0);
        Assert.All(plan, p => Assert.True(p.ApplyThisRun));
        Assert.Contains(plan, p => p.ReasonEn.Contains("full fan-out", StringComparison.Ordinal));
    }

    [Fact]
    public void Summarize_CountsBatches()
    {
        var plan = SyncTargetPlanner.BuildPlan(
            [
                new("empty", 0, true, null),
                new("pop1", 5, true, DateTimeOffset.UtcNow.AddDays(-2)),
                new("pop2", 5, true, DateTimeOffset.UtcNow),
            ],
            maxPopulatedPerSync: 1);

        var summary = SyncTargetPlanner.Summarize(plan);
        Assert.Contains("1 empty/unknown first", summary);
        Assert.Contains("1 populated this run", summary);
        Assert.Contains("1 deferred", summary);
    }
}
