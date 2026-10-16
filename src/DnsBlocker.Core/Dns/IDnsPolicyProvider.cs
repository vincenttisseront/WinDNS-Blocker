namespace DnsBlocker.Core.Dns;

public interface IDnsPolicyProvider
{
    /// <summary>
    /// Replaces all policies with the given prefix on the target with the provided domain chunks.
    /// </summary>
    Task ApplyPoliciesAsync(
        string computerName,
        string policyNamePrefix,
        string action,
        IReadOnlyList<IReadOnlyList<string>> chunks,
        CancellationToken cancellationToken = default);

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
