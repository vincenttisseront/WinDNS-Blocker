namespace DnsBlocker.Core.Ui;

/// <summary>
/// Pure rules for MainWindow refresh / domain-search behaviour (unit-tested).
/// </summary>
public static class UiRefreshPolicy
{
    /// <summary>
    /// Domain search runs on Refresh only when the Domains tab is selected and the
    /// search box has a user query. Machine-domain prefill must not trigger this.
    /// </summary>
    public static bool ShouldSearchDomainsOnRefresh(bool domainsTabSelected, string? query) =>
        domainsTabSelected && !string.IsNullOrWhiteSpace(query);
}
