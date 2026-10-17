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
    private readonly object _progressGate = new();
    private SyncProgressUpdate? _currentProgress;

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

    /// <summary>Latest phase update while <see cref="IsSyncing"/> (also mirrored to FileLogBuffer by the hub).</summary>
    public SyncProgressUpdate? CurrentProgress
    {
        get
        {
            lock (_progressGate)
                return _currentProgress;
        }
    }

    public async Task<SyncRunResult> RunAsync(
        AppConfig config,
        CancellationToken cancellationToken = default,
        IProgress<SyncProgressUpdate>? progress = null)
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
        // Yield CPU to DNS/AD on domain controllers during multi-million ingest + QRP push.
        using var priority = ProcessMemory.EnterBelowNormalPriority();
        try
        {
            var targets = config.DnsTargets
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Bridge so ingest/provider updates also refresh CurrentProgress (IPC SoftPoll).
            IProgress<SyncProgressUpdate> bridge = new SyncProgressBridge(u => Report(progress, u));

            Report(progress, SyncProgressUpdate.Starting(targets.Count));
            _logger.LogInformation(
                "Starting blocklist sync (priority BelowNormal, maxDomains={Max}, RSS {Rss})",
                config.MaxDomains <= 0 ? "unlimited" : config.MaxDomains.ToString(),
                ProcessMemory.FormatWorkingSet());

            var ingest = await _ingestor.IngestAsync(
                config.BlocklistsUrl,
                config.WhitelistsUrl,
                CuratedBlocklists.ResolveExtraBlocklistUrls(config),
                config.ExtraWhitelistUrls,
                cancellationToken,
                config.MaxDomains,
                bridge).ConfigureAwait(false);

            result.WhitelistCount = ingest.WhitelistCount;
            result.SourceListCount = ingest.SourceListCount;

            if (ingest.Errors.Count > 0)
                _logger.LogWarning("Ingest completed with {Count} source errors", ingest.Errors.Count);

            // Own the single HashSet — apex + compact in place (no second/third multi-M copies).
            var domains = ingest.Domains;
            var ingestedCount = domains.Count;
            DomainListParser.PrepareForQrpInPlace(domains);
            if (config.MaxDomains > 0 && domains.Count > config.MaxDomains)
                DomainListParser.EnforceMaxDomains(domains, config.MaxDomains);
            result.DomainCount = domains.Count;
            _logger.LogInformation(
                "Prepared QRP set: ingested={Ingested} → prepared={Prepared} (RSS {Rss})",
                ingestedCount, domains.Count, ProcessMemory.FormatWorkingSet());

            Report(progress, SyncProgressUpdate.Store(domains.Count));
            _state.ReplaceDomains(domains);
            _logger.LogInformation("SQLite domain replace done (RSS {Rss})", ProcessMemory.FormatWorkingSet());

            var chunks = DomainListParser.ChunkByFqdnBudget(
                domains,
                PowerShellDnsPolicyProvider.MaxFqdnCriterionChars,
                Math.Max(1, config.ChunkSize));
            var expectedPolicies = PowerShellDnsPolicyProvider.CountExpectedPolicies(chunks);

            // Chunks hold string refs; drop the HashSet shell + buckets so GC can reclaim.
            domains.Clear();
            domains.TrimExcess();
            ProcessMemory.TrimAfterLargeDrop(blocking: true);

            Report(progress, SyncProgressUpdate.Chunk(result.DomainCount, expectedPolicies));
            _logger.LogInformation(
                "QRP chunks={Chunks} expectedPolicies={Expected} (max {MaxPerChunk}/policy, budget {Budget} chars, RSS {Rss})",
                chunks.Count,
                expectedPolicies,
                Math.Max(1, config.ChunkSize),
                PowerShellDnsPolicyProvider.MaxFqdnCriterionChars,
                ProcessMemory.FormatWorkingSet());

            if (targets.Count == 0)
            {
                result.Error = "No DNS targets configured.";
                result.FinishedAt = DateTimeOffset.UtcNow;
                Report(progress, SyncProgressUpdate.Error(result.Error));
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
            var throttleMs = Math.Max(0, config.PolicyApplyThrottleMs);
            var gcEvery = Math.Max(0, config.PolicyApplyGcEvery);
            var tasks = result.Targets.Select(async targetResult =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                targetResult.Status = TargetSyncStatus.InProgress;
                try
                {
                    Report(progress, SyncProgressUpdate.ApplyTarget(targetResult.Target, expectedPolicies));
                    _logger.LogInformation(
                        "Applying QRP to {Target} (RSS {Rss})",
                        targetResult.Target, ProcessMemory.FormatWorkingSet());

                    await _provider.ApplyPoliciesAsync(
                        targetResult.Target,
                        config.PolicyNamePrefix,
                        config.PolicyAction,
                        chunks,
                        cancellationToken,
                        config.RestartDnsAfterPolicySync,
                        throttleMs,
                        gcEvery,
                        bridge).ConfigureAwait(false);

                    targetResult.Status = TargetSyncStatus.Succeeded;
                    targetResult.PolicyCount = expectedPolicies;
                    targetResult.Error = null;
                    Report(progress, SyncProgressUpdate.ApplyDone(targetResult.Target, expectedPolicies, ok: true));
                    _logger.LogInformation(
                        "Sync succeeded for {Target} ({Policies} policies, RSS {Rss})",
                        targetResult.Target, expectedPolicies, ProcessMemory.FormatWorkingSet());
                }
                catch (Exception ex)
                {
                    targetResult.Status = TargetSyncStatus.Failed;
                    targetResult.Error = ex.Message;
                    Report(progress, SyncProgressUpdate.ApplyDone(
                        targetResult.Target, expectedPolicies, ok: false, error: ex.Message));
                    _logger.LogError(ex, "Sync failed for {Target}", targetResult.Target);
                }
                finally
                {
                    sw.Stop();
                    targetResult.Duration = sw.Elapsed;
                    targetResult.FinishedAt = DateTimeOffset.UtcNow;
                    ProcessMemory.TrimAfterLargeDrop(blocking: true);
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Drop chunk lists so domain strings are eligible for GC (manifest already on disk during apply).
            if (chunks is List<IReadOnlyList<string>> mutable)
                mutable.Clear();
            ProcessMemory.TrimAfterLargeDrop(blocking: true);

            result.FinishedAt = DateTimeOffset.UtcNow;
            _state.SaveSyncResult(result);
            // HubRuntime emits Done after post-sync QRP probe / block test.
            _logger.LogInformation("Sync finished (RSS {Rss})", ProcessMemory.FormatWorkingSet());
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            result.FinishedAt = DateTimeOffset.UtcNow;
            _state.SaveSyncResult(result);
            Report(progress, SyncProgressUpdate.Error(ex.Message));
            _logger.LogError(ex, "Sync failed");
            return result;
        }
        finally
        {
            IsSyncing = false;
            lock (_progressGate)
                _currentProgress = null;
            _syncLock.Release();
        }
    }

    private void Report(IProgress<SyncProgressUpdate>? progress, SyncProgressUpdate update)
    {
        lock (_progressGate)
            _currentProgress = update;
        progress?.Report(update);
    }

    private sealed class SyncProgressBridge : IProgress<SyncProgressUpdate>
    {
        private readonly Action<SyncProgressUpdate> _onReport;
        public SyncProgressBridge(Action<SyncProgressUpdate> onReport) => _onReport = onReport;
        public void Report(SyncProgressUpdate value) => _onReport(value);
    }
}
