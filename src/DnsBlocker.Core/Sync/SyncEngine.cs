using DnsBlocker.Core.Diagnostics;
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
            _logger.LogInformation("Starting blocklist sync (RSS {Rss})", ProcessMemory.FormatWorkingSet());
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

            // Own the single HashSet — apex + compact in place (no second/third 6M copies).
            var domains = ingest.Domains;
            var ingestedCount = domains.Count;
            DomainListParser.PrepareForQrpInPlace(domains);
            result.DomainCount = domains.Count;
            _logger.LogInformation(
                "Prepared QRP set: ingested={Ingested} → prepared={Prepared} (RSS {Rss})",
                ingestedCount, domains.Count, ProcessMemory.FormatWorkingSet());

            _state.ReplaceDomains(domains);
            _logger.LogInformation("SQLite domain replace done (RSS {Rss})", ProcessMemory.FormatWorkingSet());

            var chunks = DomainListParser.ChunkByFqdnBudget(
                domains,
                PowerShellDnsPolicyProvider.MaxFqdnCriterionChars,
                Math.Max(1, config.ChunkSize));
            var expectedPolicies = PowerShellDnsPolicyProvider.CountExpectedPolicies(chunks);

            // Chunks hold string refs; drop the HashSet shell so GC can reclaim bucket arrays.
            domains.Clear();
            ProcessMemory.TrimAfterLargeDrop();

            _logger.LogInformation(
                "QRP chunks={Chunks} expectedPolicies={Expected} (max {MaxPerChunk}/policy, budget {Budget} chars, RSS {Rss})",
                chunks.Count,
                expectedPolicies,
                Math.Max(1, config.ChunkSize),
                PowerShellDnsPolicyProvider.MaxFqdnCriterionChars,
                ProcessMemory.FormatWorkingSet());

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
                    DomainCount = result.DomainCount
                });
            }

            // Default 1 — parallel apply doubles PowerShell + manifest RSS on the DC.
            using var gate = new SemaphoreSlim(Math.Max(1, config.MaxParallelTargets));
            var tasks = result.Targets.Select(async targetResult =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                targetResult.Status = TargetSyncStatus.InProgress;
                try
                {
                    _logger.LogInformation(
                        "Applying QRP to {Target} (RSS {Rss})",
                        targetResult.Target, ProcessMemory.FormatWorkingSet());
                    await _provider.ApplyPoliciesAsync(
                        targetResult.Target,
                        config.PolicyNamePrefix,
                        config.PolicyAction,
                        chunks,
                        cancellationToken,
                        config.RestartDnsAfterPolicySync).ConfigureAwait(false);

                    targetResult.Status = TargetSyncStatus.Succeeded;
                    targetResult.PolicyCount = expectedPolicies;
                    targetResult.Error = null;
                    _logger.LogInformation(
                        "Sync succeeded for {Target} ({Policies} policies, RSS {Rss})",
                        targetResult.Target, expectedPolicies, ProcessMemory.FormatWorkingSet());
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
                    ProcessMemory.TrimAfterLargeDrop();
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            result.FinishedAt = DateTimeOffset.UtcNow;
            _state.SaveSyncResult(result);
            _logger.LogInformation("Sync finished (RSS {Rss})", ProcessMemory.FormatWorkingSet());
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
