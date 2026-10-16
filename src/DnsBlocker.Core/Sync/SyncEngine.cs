using DnsBlocker.Core.Dns;
using DnsBlocker.Core.Ingest;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Net;
using DnsBlocker.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnsBlocker.Core.Sync;

public sealed class SyncEngine
{
    private readonly BlocklistIngestor _ingestor;
    private readonly IDnsPolicyProvider _provider;
    private readonly StateStore _state;
    private readonly ILogger<SyncEngine> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public SyncEngine(
        BlocklistIngestor ingestor,
        IDnsPolicyProvider provider,
        StateStore state,
        ILogger<SyncEngine>? logger = null)
    {
        _ingestor = ingestor;
        _provider = provider;
        _state = state;
        _logger = logger ?? NullLogger<SyncEngine>.Instance;
    }

    public bool IsSyncing { get; private set; }

    public async Task<SyncRunResult> RunAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        if (!await _syncLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new SyncRunResult
            {
                Error = "A sync is already in progress.",
                FinishedAt = DateTimeOffset.UtcNow
            };
        }

        IsSyncing = true;
        var result = new SyncRunResult { StartedAt = DateTimeOffset.UtcNow };
        try
        {
            _logger.LogInformation("Starting blocklist sync");
            var ingest = await _ingestor.IngestAsync(
                config.BlocklistsUrl,
                config.WhitelistsUrl,
                CuratedBlocklists.ResolveExtraBlocklistUrls(config),
                config.ExtraWhitelistUrls,
                cancellationToken).ConfigureAwait(false);

            result.WhitelistCount = ingest.WhitelistCount;
            result.SourceListCount = ingest.SourceListCount;

            if (ingest.Errors.Count > 0)
                _logger.LogWarning("Ingest completed with {Count} source errors", ingest.Errors.Count);

            // QRP EQ,*.apex may not match apex on all builds — expand bare apex.
            var expanded = DomainListParser.EnsureApexForWildcards(ingest.Domains);
            result.DomainCount = expanded.Count;
            _state.ReplaceDomains(expanded);

            // Drop hostnames already covered by *.apex wildcards before chunking (keeps apex).
            var forQrp = DomainListParser.CompactCoveredByWildcards(expanded);
            var chunks = DomainListParser.ChunkByFqdnBudget(
                forQrp,
                PowerShellDnsPolicyProvider.MaxFqdnCriterionChars,
                Math.Max(1, config.ChunkSize));
            _logger.LogInformation(
                "Prepared {Ingested} ingested → {Expanded} after apex → {ForQrp} after wildcard compact in {Chunks} QRP chunks (budget {Budget} chars, max {MaxPerChunk}/policy)",
                ingest.Domains.Count,
                expanded.Count,
                forQrp.Count,
                chunks.Count,
                PowerShellDnsPolicyProvider.MaxFqdnCriterionChars,
                Math.Max(1, config.ChunkSize));

            var targets = config.DnsTargets
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (targets.Count == 0)
            {
                result.Error = "No DNS targets configured.";
                result.FinishedAt = DateTimeOffset.UtcNow;
                _state.SaveSyncResult(result);
                return result;
            }

            foreach (var t in targets)
            {
                result.Targets.Add(new TargetSyncResult
                {
                    Target = t,
                    IsLocal = LocalDnsTarget.IsLocalTarget(t),
                    Status = TargetSyncStatus.Idle,
                    DomainCount = ingest.Domains.Count
                });
            }

            using var gate = new SemaphoreSlim(Math.Max(1, config.MaxParallelTargets));
            var tasks = result.Targets.Select(async targetResult =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                targetResult.Status = TargetSyncStatus.InProgress;
                try
                {
                    await _provider.ApplyPoliciesAsync(
                        targetResult.Target,
                        config.PolicyNamePrefix,
                        config.PolicyAction,
                        chunks,
                        cancellationToken,
                        config.RestartDnsAfterPolicySync).ConfigureAwait(false);

                    targetResult.Status = TargetSyncStatus.Succeeded;
                    targetResult.PolicyCount = chunks.Count;
                    targetResult.Error = null;
                    _logger.LogInformation("Sync succeeded for {Target}", targetResult.Target);
                }
                catch (Exception ex)
                {
                    targetResult.Status = TargetSyncStatus.Failed;
                    targetResult.Error = ex.Message;
                    _logger.LogError(ex, "Sync failed for {Target}", targetResult.Target);
                }
                finally
                {
                    sw.Stop();
                    targetResult.Duration = sw.Elapsed;
                    targetResult.FinishedAt = DateTimeOffset.UtcNow;
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            result.FinishedAt = DateTimeOffset.UtcNow;
            _state.SaveSyncResult(result);
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.FinishedAt = DateTimeOffset.UtcNow;
            _state.SaveSyncResult(result);
            _logger.LogError(ex, "Sync failed");
            return result;
        }
        finally
        {
            IsSyncing = false;
            _syncLock.Release();
        }
    }
}
