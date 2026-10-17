using System.Collections.Concurrent;
using DnsBlocker.Core.Sync;

namespace DnsBlocker.Core.Dns;

/// <summary>
/// In-memory provider for tests and non-Windows development.
/// Configure <see cref="FailingTargets"/> to simulate partial farm failures.
/// </summary>
public sealed class MockDnsPolicyProvider : IDnsPolicyProvider
{
    private readonly ConcurrentDictionary<string, List<PolicyRecord>> _store = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FailingTargets { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Targets that throw on ListPoliciesAsync (probe failure).</summary>
    public HashSet<string> ProbeFailingTargets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlyList<PolicyRecord>> Snapshot =>
        _store.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<PolicyRecord>)kv.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);

    public Task ApplyPoliciesAsync(
        string computerName,
        string policyNamePrefix,
        string action,
        IReadOnlyList<IReadOnlyList<string>> chunks,
        CancellationToken cancellationToken = default,
        bool restartDnsAfterApply = true,
        int applyThrottleMs = 15,
        int applyGcEvery = 25,
        IProgress<SyncProgressUpdate>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = restartDnsAfterApply;
        _ = applyThrottleMs;
        _ = applyGcEvery;

        if (FailingTargets.Contains(computerName))
            throw new InvalidOperationException($"Mock failure for target '{computerName}'.");

        var expected = PowerShellDnsPolicyProvider.CountExpectedPolicies(chunks);
        progress?.Report(SyncProgressUpdate.ApplyProgress(computerName, 0, expected));

        var policies = new List<PolicyRecord>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var name = PowerShellDnsPolicyProvider.FormatPolicyName(policyNamePrefix, i + 1);
            policies.Add(new PolicyRecord(name, action, chunks[i].ToList(), IsEnabled: true));
            if (expected > 0 && ((i + 1) % Math.Max(1, expected / 4) == 0 || i + 1 == chunks.Count))
                progress?.Report(SyncProgressUpdate.ApplyProgress(computerName, i + 1, expected));
        }

        _store[computerName] = policies;
        progress?.Report(SyncProgressUpdate.ApplyProgress(computerName, expected, expected));
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<string>> ListPolicyNamesAsync(
        string computerName,
        string policyNamePrefix,
        CancellationToken cancellationToken = default)
    {
        var policies = await ListPoliciesAsync(computerName, policyNamePrefix, cancellationToken)
            .ConfigureAwait(false);
        return policies.Select(p => p.Name).ToList();
    }

    public Task<IReadOnlyList<DnsPolicyInfo>> ListPoliciesAsync(
        string computerName,
        string policyNamePrefix,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (ProbeFailingTargets.Contains(computerName))
            throw new InvalidOperationException($"Mock probe failure for target '{computerName}'.");

        if (!_store.TryGetValue(computerName, out var policies))
            return Task.FromResult<IReadOnlyList<DnsPolicyInfo>>([]);

        return Task.FromResult<IReadOnlyList<DnsPolicyInfo>>(
            policies
                .Where(p => p.Name.StartsWith(policyNamePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(p => new DnsPolicyInfo
                {
                    Name = p.Name,
                    IsEnabled = p.IsEnabled,
                    Action = p.Action,
                    FqdnCriterionLength = p.Domains.Count == 0
                        ? 0
                        : ("EQ," + string.Join(",", p.Domains)).Length
                })
                .ToList());
    }

    /// <summary>Test helper: toggle enabled state of an existing policy.</summary>
    public void SetPolicyEnabled(string computerName, string policyName, bool enabled)
    {
        if (!_store.TryGetValue(computerName, out var policies))
            return;

        for (var i = 0; i < policies.Count; i++)
        {
            if (policies[i].Name.Equals(policyName, StringComparison.OrdinalIgnoreCase))
            {
                policies[i] = policies[i] with { IsEnabled = enabled };
                return;
            }
        }
    }

    public sealed record PolicyRecord(
        string Name,
        string Action,
        IReadOnlyList<string> Domains,
        bool IsEnabled = true);
}
