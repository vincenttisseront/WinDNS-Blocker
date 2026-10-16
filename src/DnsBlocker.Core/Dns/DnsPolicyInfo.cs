namespace DnsBlocker.Core.Dns;

/// <summary>
/// Live inventory of one DNS Query Resolution Policy on a target server.
/// </summary>
public sealed class DnsPolicyInfo
{
    public required string Name { get; init; }

    /// <summary>True when the policy is enabled on the remote DNS server.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>DENY, IGNORE, or other QRP action when reported.</summary>
    public string? Action { get; init; }
}
