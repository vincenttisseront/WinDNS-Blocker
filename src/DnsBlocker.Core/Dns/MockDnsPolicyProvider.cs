using System.Collections.Concurrent;

namespace DnsBlocker.Core.Dns;

/// <summary>
/// In-memory provider for tests and non-Windows development.
/// Configure <see cref="FailingTargets"/> to simulate partial farm failures.
/// </summary>
public sealed class MockDnsPolicyProvider : IDnsPolicyProvider
{
    private readonly ConcurrentDictionary<string, List<PolicyRecord>> _store = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> FailingTargets { get; } = new(StringComparer.OrdinalIgnoreCase);

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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (FailingTargets.Contains(computerName))
            throw new InvalidOperationException($"Mock failure for target '{computerName}'.");

        var policies = new List<PolicyRecord>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var name = $"{policyNamePrefix}{(i + 1):D4}";
            policies.Add(new PolicyRecord(name, action, chunks[i].ToList()));
        }

        _store[computerName] = policies;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListPolicyNamesAsync(
        string computerName,
        string policyNamePrefix,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.TryGetValue(computerName, out var policies))
            return Task.FromResult<IReadOnlyList<string>>([]);

        return Task.FromResult<IReadOnlyList<string>>(
            policies.Where(p => p.Name.StartsWith(policyNamePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .ToList());
    }

    public sealed record PolicyRecord(string Name, string Action, IReadOnlyList<string> Domains);
}
