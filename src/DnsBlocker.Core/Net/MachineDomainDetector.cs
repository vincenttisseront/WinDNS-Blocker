using System.Net.NetworkInformation;

namespace DnsBlocker.Core.Net;

/// <summary>
/// Resolves the DNS / AD domain suffix of the machine running the hub UI or service.
/// Never hardcodes a customer domain — returns empty for workgroup machines.
/// </summary>
public static class MachineDomainDetector
{
    /// <summary>
    /// Optional override for tests. When set, <see cref="GetDnsDomain"/> returns this value.
    /// </summary>
    public static Func<string?>? ResolverOverride { get; set; }

    public static string GetDnsDomain()
    {
        if (ResolverOverride is not null)
            return Normalize(ResolverOverride() ?? "");

        try
        {
            var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            return Normalize(domain);
        }
        catch
        {
            return "";
        }
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var d = value.Trim().Trim('.').ToLowerInvariant();
        // Treat single-label / localhost-ish as no domain (workgroup)
        if (d is "local" or "localhost" or "(none)" or "workgroup")
            return "";
        if (!d.Contains('.'))
            return "";
        return d;
    }
}
