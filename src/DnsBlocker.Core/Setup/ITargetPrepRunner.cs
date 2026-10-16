using DnsBlocker.Core.Models;

namespace DnsBlocker.Core.Setup;

public interface ITargetPrepRunner
{
    Task<TargetWinRmTestResult> TestWinRmAsync(TargetWinRmTestRequest request, CancellationToken cancellationToken = default);

    Task<PrepareDnsTargetResult> PrepareAsync(PrepareDnsTargetRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prepare the local DNS Server (registry ACL + QRP probe) without WinRM / credentials.
    /// </summary>
    Task<PrepareDnsTargetResult> PrepareLocalAsync(
        string hostname,
        CancellationToken cancellationToken = default);
}
