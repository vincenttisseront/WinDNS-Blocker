using DnsBlocker.Core.Sync;

namespace DnsBlocker.Core.Dns;

public interface IDnsPolicyProvider
{
    /// <summary>
    /// Replaces all policies with the given prefix on the target with the provided domain chunks.
    /// Implementations should verify live Enabled count after apply; <paramref name="restartDnsAfterApply"/>
    /// restarts the Windows DNS Server service so new FQDN criteria enter the query path.
    /// <paramref name="applyThrottleMs"/> / <paramref name="applyGcEvery"/> throttle PowerShell/CIM
    /// Adds on domain controllers (sleep + GC every N policies).
    /// <paramref name="progress"/> receives phase-level apply milestones (not per-domain).
    /// </summary>
    Task ApplyPoliciesAsync(
        string computerName,
        string policyNamePrefix,
        string action,
        IReadOnlyList<IReadOnlyList<string>> chunks,
        CancellationToken cancellationToken = default,
        bool restartDnsAfterApply = true,
        int applyThrottleMs = 15,
        int applyGcEvery = 25,
        IProgress<SyncProgressUpdate>? progress = null);

    Task<IReadOnlyList<string>> ListPolicyNamesAsync(
        string computerName,
        string policyNamePrefix,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live inventory via Get-DnsServerQueryResolutionPolicy (name, IsEnabled, Action).
    /// </summary>
    Task<IReadOnlyList<DnsPolicyInfo>> ListPoliciesAsync(
        string computerName,
        string policyNamePrefix,
        CancellationToken cancellationToken = default);
}
