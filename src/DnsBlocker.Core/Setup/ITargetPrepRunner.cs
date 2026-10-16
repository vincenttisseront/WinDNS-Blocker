using DnsBlocker.Core.Models;

namespace DnsBlocker.Core.Setup;

public interface ITargetPrepRunner
{
    Task<TargetWinRmTestResult> TestWinRmAsync(TargetWinRmTestRequest request, CancellationToken cancellationToken = default);

    Task<PrepareDnsTargetResult> PrepareAsync(PrepareDnsTargetRequest request, CancellationToken cancellationToken = default);
}
