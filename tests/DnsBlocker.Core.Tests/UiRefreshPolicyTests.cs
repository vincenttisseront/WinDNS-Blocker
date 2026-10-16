using DnsBlocker.Core.Ui;

namespace DnsBlocker.Core.Tests;

public class UiRefreshPolicyTests
{
    [Theory]
    [InlineData(false, "ar-systems.fr", false)]
    [InlineData(false, "", false)]
    [InlineData(false, null, false)]
    [InlineData(true, "", false)]
    [InlineData(true, "   ", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "ar-systems.fr", true)]
    [InlineData(true, "beeg.com", true)]
    public void ShouldSearchDomainsOnRefresh_only_when_domains_tab_has_query(
        bool domainsTabSelected, string? query, bool expected)
    {
        Assert.Equal(expected, UiRefreshPolicy.ShouldSearchDomainsOnRefresh(domainsTabSelected, query));
    }
}
