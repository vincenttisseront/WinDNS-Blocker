namespace DnsBlocker.Core.Dns;

public interface IDnsPolicyProvider
{
    /// <summary>
    /// Replaces all policies with the given prefix on the target with the provided domain chunks.
    /// Implementations should verify live Enabled count after apply; <paramref name="restartDnsAfterApply"/>
    /// restarts the Windows DNS Server service so new FQDN criteria enter the query path.
    /// </summary>
    Task ApplyPoliciesAsync(
        string computerName,
        string policyNamePrefix,
        string action,
        IReadOnlyList<IReadOnlyList<string>> chunks,
        CancellationToken cancellationToken = default,
        bool restartDnsAfterApply = true);

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
