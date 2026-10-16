using DnsBlocker.Core.Net;

namespace DnsBlocker.Core.Tests;

public class MachineDomainDetectorTests : IDisposable
{
    public void Dispose() => MachineDomainDetector.ResolverOverride = null;

    [Theory]
    [InlineData("AR-SYSTEMS.FR", "ar-systems.fr")]
    [InlineData("contoso.com.", "contoso.com")]
    [InlineData("", "")]
    [InlineData("WORKGROUP", "")]
    [InlineData("local", "")]
    [InlineData("myserver", "")]
    public void Normalize_And_GetDnsDomain_HandleWorkgroupAndFqdn(string raw, string expected)
    {
        MachineDomainDetector.ResolverOverride = () => raw;
        Assert.Equal(expected, MachineDomainDetector.GetDnsDomain());
    }
}
