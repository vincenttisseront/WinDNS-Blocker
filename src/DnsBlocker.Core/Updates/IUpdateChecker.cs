namespace DnsBlocker.Core.Updates;

public interface IUpdateChecker
{
    /// <summary>
    /// GETs the version feed URL and compares the manifest version to
    /// <paramref name="currentVersion"/> (defaults to <see cref="AppVersion.Current"/>).
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(
        string feedUrl,
        string? currentVersion = null,
        CancellationToken cancellationToken = default);
}
