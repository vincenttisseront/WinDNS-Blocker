using DnsBlocker.Core.Config;
using DnsBlocker.Core.Dns;
using DnsBlocker.Core.Logging;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Security;
using DnsBlocker.Core.Setup;
using DnsBlocker.Core.Stats;
using DnsBlocker.Core.Storage;
using DnsBlocker.Core.Sync;
using DnsBlocker.Core.Updates;

namespace DnsBlocker.Service;

/// <summary>
/// Shared runtime state used by the hosted service and IPC handlers.
/// </summary>
public sealed class HubRuntime
{
    private readonly ConfigStore _configStore;
    private readonly SyncEngine _syncEngine;
    private readonly IDnsPolicyProvider _dnsProvider;
    private readonly StateStore _state;
    private readonly FileLogBuffer _logs;
    private readonly DnsStatsCollector _stats;
    private readonly IUpdateChecker _updateChecker;
    private readonly UpdateInstaller _updateInstaller;
    private readonly ITargetPrepRunner _targetPrep;
    private readonly SecureCredentialStore _credentials;
    private readonly PreparedTargetStore _preparedTargets;
    private readonly object _configGate = new();
    private readonly object _updateGate = new();
    private readonly object _probeGate = new();
    private AppConfig _config;
    private UpdateCheckResult? _lastUpdateCheck;
    private TargetPolicyStatusResult? _lastPolicyProbe;

    public HubRuntime(
        ConfigStore configStore,
        SyncEngine syncEngine,
        IDnsPolicyProvider dnsProvider,
        StateStore state,
        FileLogBuffer logs,
        DnsStatsCollector stats,
        IUpdateChecker updateChecker,
        UpdateInstaller updateInstaller,
        ITargetPrepRunner targetPrep,
        SecureCredentialStore credentials,
        PreparedTargetStore preparedTargets)
    {
        _configStore = configStore;
        _syncEngine = syncEngine;
        _dnsProvider = dnsProvider;
        _state = state;
        _logs = logs;
        _stats = stats;
        _updateChecker = updateChecker;
        _updateInstaller = updateInstaller;
        _targetPrep = targetPrep;
        _credentials = credentials;
        _preparedTargets = preparedTargets;
        _config = _configStore.Load();
    }

    public AppConfig GetConfig()
    {
        lock (_configGate)
            return Clone(_config);
    }

    public AppConfig SetConfig(AppConfig config)
    {
        lock (_configGate)
        {
            Normalize(config);
            _configStore.Save(config);
            _config = Clone(config);
            _logs.Write($"Config updated. Targets: {string.Join(", ", _config.DnsTargets)}");
            ApplyStatsSetting();
            return Clone(_config);
        }
    }

    public void ApplyStatsSetting()
    {
        var cfg = GetConfig();
        if (cfg.EnableStatsCollection)
            _stats.Start();
        else
            _stats.Stop();
    }

    public async Task<SyncRunResult> ApplyNowAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        _logs.Write($"Sync started for {config.DnsTargets.Count} target(s)");
        var result = await _syncEngine.RunAsync(config, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
            _logs.Write($"Sync error: {result.Error}");
        foreach (var t in result.Targets)
        {
            _logs.Write(t.Status == TargetSyncStatus.Succeeded
                ? $"Target OK: {t.Target} ({t.PolicyCount} policies)"
                : $"Target FAIL: {t.Target} — {t.Error}");
        }

        return result;
    }

    public ServiceStatus GetStatus(DateTimeOffset? nextScheduledUtc)
    {
        TargetPolicyStatusResult? lastProbe;
        lock (_probeGate)
            lastProbe = _lastPolicyProbe;

        return new ServiceStatus
        {
            IsSyncing = _syncEngine.IsSyncing,
            LastRun = _state.LoadLastSync(),
            NextScheduledUtc = nextScheduledUtc,
            ActiveDomainCount = _state.CountDomains(),
            RecentLogs = _logs.GetRecent(80).ToList(),
            StatsEnabled = _stats.IsEnabled,
            StatsMessage = _stats.Message,
            LastPolicyProbe = lastProbe
        };
    }

    /// <summary>
    /// Probes each configured DNS target with Get-DnsServerQueryResolutionPolicy and
    /// merges the live inventory with the last sync result (sync success ≠ policies installed).
    /// </summary>
    public async Task<TargetPolicyStatusResult> GetTargetPolicyStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var config = GetConfig();
        var lastSync = _state.LoadLastSync();
        var syncByTarget = (lastSync?.Targets ?? [])
            .ToDictionary(t => t.Target, t => t, StringComparer.OrdinalIgnoreCase);

        var checkedAt = DateTimeOffset.UtcNow;
        var results = new List<TargetPolicyStatus>();
        _logs.Write($"Policy probe started for {config.DnsTargets.Count} target(s)");

        foreach (var target in config.DnsTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            syncByTarget.TryGetValue(target, out var sync);

            try
            {
                var policies = await _dnsProvider
                    .ListPoliciesAsync(target, config.PolicyNamePrefix, cancellationToken)
                    .ConfigureAwait(false);
                var enabled = policies.Count(p => p.IsEnabled);
                var row = new TargetPolicyStatus
                {
                    Target = target,
                    LastSyncStatus = sync?.Status ?? TargetSyncStatus.Idle,
                    LastSyncFinishedAt = sync?.FinishedAt,
                    LastSyncError = sync?.Error,
                    LastSyncPolicyCount = sync?.PolicyCount ?? 0,
                    LivePolicyCount = policies.Count,
                    LiveEnabledCount = enabled,
                    LivePolicyNames = policies.Select(p => p.Name).ToList(),
                    ProbeOk = true,
                    CheckedAt = checkedAt
                };
                results.Add(row);
                _logs.Write(
                    $"Policy probe OK: {target} — {policies.Count} installée(s), {enabled} activée(s)" +
                    $" (sync: {row.LastSyncStatus}, {row.LastSyncPolicyCount}) → {row.AppliedLabel}");
            }
            catch (Exception ex)
            {
                var row = new TargetPolicyStatus
                {
                    Target = target,
                    LastSyncStatus = sync?.Status ?? TargetSyncStatus.Idle,
                    LastSyncFinishedAt = sync?.FinishedAt,
                    LastSyncError = sync?.Error,
                    LastSyncPolicyCount = sync?.PolicyCount ?? 0,
                    ProbeOk = false,
                    ProbeError = ex.Message,
                    CheckedAt = checkedAt
                };
                results.Add(row);
                _logs.Write($"Policy probe FAIL: {target} — {ex.Message}");
            }
        }

        var result = new TargetPolicyStatusResult
        {
            CheckedAt = checkedAt,
            Targets = results,
            Message = results.Count == 0
                ? "Aucune cible DNS configurée."
                : null
        };

        lock (_probeGate)
            _lastPolicyProbe = result;

        return result;
    }

    public IReadOnlyList<string> SearchDomains(string? query, int limit) =>
        _state.SearchDomains(query, Math.Clamp(limit, 1, 1000));

    public StatsSnapshot GetStats(int hours, int topN = 20)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        topN = Math.Clamp(topN, 1, 100);

        var to = DateTimeOffset.UtcNow;
        var from = to.AddHours(-hours);
        var snap = _state.GetStats(from, to, topN);
        var diagnosis = BuildStatsEmptyDiagnosis(snap.TotalQueries);
        if (diagnosis is null)
            return snap;

        return new StatsSnapshot
        {
            FromUtc = snap.FromUtc,
            ToUtc = snap.ToUtc,
            TotalQueries = snap.TotalQueries,
            BlockedQueries = snap.BlockedQueries,
            TopN = snap.TopN,
            TopQueriedDomains = snap.TopQueriedDomains,
            TopBlockedDomains = snap.TopBlockedDomains,
            TopClients = snap.TopClients,
            Message = diagnosis
        };
    }

    /// <summary>
    /// Turns on hub stats collection and enables Microsoft-Windows-DNSServer/Analytical on each DNS target.
    /// </summary>
    public async Task<EnableStatsCollectionResult> EnableStatsCollectionAsync(
        CancellationToken cancellationToken = default)
    {
        var cfg = GetConfig();
        cfg.EnableStatsCollection = true;
        SetConfig(cfg);

        _logs.Write($"Stats: enabling analytical logging on {cfg.DnsTargets.Count} target(s)");
        var result = await _stats.EnableOnTargetsAsync(cfg.DnsTargets, cancellationToken).ConfigureAwait(false);
        foreach (var t in result.Targets)
        {
            _logs.Write(t.Ok
                ? $"Stats analytical OK: {t.Target} — {t.Message}"
                : $"Stats analytical FAIL: {t.Target} — {t.Message}");
        }

        // Immediate poll so Load can show data after the first queries arrive.
        try
        {
            await _stats.PollTargetsAsync(cfg.DnsTargets, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logs.Write($"Stats initial poll: {ex.Message}");
        }

        result = new EnableStatsCollectionResult
        {
            StatsEnabled = true,
            Message = _stats.Message,
            Targets = _stats.LastTargetStatus.ToList()
        };
        return result;
    }

    public async Task PollStatsAsync(CancellationToken cancellationToken = default)
    {
        if (!_stats.IsEnabled)
            return;

        var cfg = GetConfig();
        if (cfg.DnsTargets.Count == 0)
            return;

        var results = await _stats.PollTargetsAsync(cfg.DnsTargets, cancellationToken).ConfigureAwait(false);
        var ingested = results.Sum(r => r.EventsIngested);
        if (ingested > 0)
            _logs.Write($"Stats poll: ingested {ingested} event(s) from {results.Count} target(s)");
    }

    public async Task<BlockTestResult> TestDnsBlockAsync(
        string? domain,
        string? target,
        CancellationToken cancellationToken = default)
    {
        var cfg = GetConfig();
        var dnsTarget = string.IsNullOrWhiteSpace(target)
            ? cfg.DnsTargets.FirstOrDefault()
            : target.Trim();
        if (string.IsNullOrWhiteSpace(dnsTarget))
        {
            return new BlockTestResult
            {
                Target = "",
                Domain = domain ?? "",
                InBlocklist = false,
                ProbeOk = false,
                AppearsBlocked = false,
                Error = "No DNS target configured.",
                Guidance = "Add windc05.ar-systems.fr (or your DNS farm host) under Configuration."
            };
        }

        var name = string.IsNullOrWhiteSpace(domain)
            ? _state.PickSampleBlockedDomain()
            : domain.Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name))
        {
            return new BlockTestResult
            {
                Target = dnsTarget,
                Domain = "",
                InBlocklist = false,
                ProbeOk = false,
                AppearsBlocked = false,
                Error = "No domain to test — sync a blocklist first or enter a domain.",
                Guidance = "Run Update now, then test a domain that appears under Domains."
            };
        }

        var inList = _state.IsBlocked(name);
        _logs.Write($"Block test: {name} via {dnsTarget} (in blocklist={inList})");
        var result = await _stats.TestBlockAsync(dnsTarget, name, inList, cancellationToken).ConfigureAwait(false);
        _logs.Write(
            $"Block test result: {result.Domain} on {result.Target} — " +
            $"blocked={result.AppearsBlocked}, summary={result.ResolutionSummary ?? result.Error}");
        return result;
    }

    private string? BuildStatsEmptyDiagnosis(long totalQueries)
    {
        if (totalQueries > 0)
            return null;

        var cfg = GetConfig();
        var parts = new List<string>();

        if (!cfg.EnableStatsCollection || !_stats.IsEnabled)
        {
            parts.Add("Collector OFF — use Stats → « Enable collection on DNS targets » (or Configuration checkbox + Save).");
        }
        else
        {
            parts.Add("Collector ON — " + _stats.Message);
        }

        if (cfg.DnsTargets.Count == 0)
            parts.Add("No DNS targets configured.");
        else
            parts.Add("Targets: " + string.Join(", ", cfg.DnsTargets) + ".");

        var meta = _state.ListStatsIngestMeta();
        if (meta.Count == 0 && _stats.IsEnabled)
            parts.Add("Analytical logging not enabled yet on targets — click Enable collection.");
        foreach (var m in meta)
        {
            if (!string.IsNullOrWhiteSpace(m.LastError))
                parts.Add($"{m.Target}: ingest error — {m.LastError}");
            else if (!m.AnalyticalEnabled)
                parts.Add($"{m.Target}: analytical channel not confirmed enabled.");
            else if (m.LastEventCount == 0 && m.LastEventUtc is null)
                parts.Add($"{m.Target}: analytical on, but no query events seen.");
        }

        parts.Add(
            "Clients (phone/PC) must use a farm DNS as their resolver (e.g. windc05.ar-systems.fr). " +
            "Hub host windc04 alone does not receive those queries. Disable DoH / private DNS on the client.");

        return string.Join(" ", parts);
    }

    public DateTimeOffset ComputeNextScheduledUtc(DateTimeOffset nowLocal)
    {
        var cfg = GetConfig();
        var today = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day,
            cfg.ScheduledHour, cfg.ScheduledMinute, 0, nowLocal.Offset);
        var next = today <= nowLocal ? today.AddDays(1) : today;
        return next.ToUniversalTime();
    }

    public string GetAppVersion() => AppVersion.Current;

    public UpdateCheckResult? GetLastUpdateCheck()
    {
        lock (_updateGate)
            return _lastUpdateCheck;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var cfg = GetConfig();
        try
        {
            var result = await _updateChecker.CheckAsync(cfg.UpdateFeedUrl, AppVersion.Current, cancellationToken)
                .ConfigureAwait(false);
            lock (_updateGate)
                _lastUpdateCheck = result;
            if (result.UpdateAvailable)
                _logs.Write($"Update available: {result.Manifest?.Version} (current {result.CurrentVersion})");
            else
                _logs.Write(result.Message ?? "No update available");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // UpdateChecker already swallows most failures; keep IPC from ever faulting the pipe.
            var failed = new UpdateCheckResult
            {
                UpdateAvailable = false,
                FeedError = true,
                CurrentVersion = AppVersion.Current,
                Message = "Échec de la vérification des mises à jour. / Update check failed. " + ex.Message
            };
            lock (_updateGate)
                _lastUpdateCheck = failed;
            _logs.Write($"Update check failed: {ex.Message}");
            return failed;
        }
    }

    /// <summary>
    /// Installs an update via msiexec. Prefer silent=true when called from the service
    /// (already LocalSystem). Do not stop the service manually — the MSI ServiceControl
    /// table should stop/restart DnsBlocker during the upgrade.
    /// Never kills the host process after starting msiexec; failures return <see cref="UpdateInstallResult"/>.
    /// </summary>
    public async Task<UpdateInstallResult> InstallUpdateAsync(
        UpdateManifest? manifest = null,
        bool silent = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (manifest is null)
            {
                var check = await CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
                if (!check.UpdateAvailable || check.Manifest is null)
                {
                    return new UpdateInstallResult
                    {
                        Started = false,
                        Success = false,
                        Error = check.Message ?? "No update available to install."
                    };
                }

                manifest = check.Manifest;
            }

            _logs.Write($"Starting MSI update {manifest.Version} (silent={silent})");
            var result = await _updateInstaller.InstallAsync(manifest, silent, cancellationToken).ConfigureAwait(false);
            if (result.Started)
                _logs.Write($"msiexec started for {manifest.Version} — service may stop via MSI ServiceControl; host process not terminated");
            else
                _logs.Write($"Update install failed: {result.Error}");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logs.Write($"Update install exception: {ex.Message}");
            return new UpdateInstallResult
            {
                Started = false,
                Success = false,
                Error = "Échec de l'installation de la mise à jour. / Update install failed. " + ex.Message
            };
        }
    }

    /// <summary>
    /// Daily update path: check feed when configured; auto-install only if AutoInstallUpdates.
    /// </summary>
    public async Task RunScheduledUpdateCheckAsync(CancellationToken cancellationToken = default)
    {
        var cfg = GetConfig();
        if (string.IsNullOrWhiteSpace(cfg.UpdateFeedUrl))
            return;

        try
        {
            var check = await CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
            if (check.UpdateAvailable && cfg.AutoInstallUpdates && check.Manifest is not null)
            {
                _logs.Write("AutoInstallUpdates: launching silent msiexec");
                await InstallUpdateAsync(check.Manifest, silent: true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logs.Write($"Scheduled update check failed: {ex.Message}");
        }
    }

    public Task<TargetWinRmTestResult> TestTargetWinRmAsync(
        TargetWinRmTestRequest request,
        CancellationToken cancellationToken = default)
    {
        _logs.Write($"WinRM test → {request.Hostname} (user={request.Username})");
        // Never log password
        return _targetPrep.TestWinRmAsync(request, cancellationToken);
    }

    public async Task<PrepareDnsTargetResult> PrepareDnsTargetAsync(
        PrepareDnsTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        _logs.Write($"Préparation DNS cible → {request.Hostname} (user={request.Username}, dnsAdmins={request.AddHubToDnsAdmins})");
        var result = await _targetPrep.PrepareAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.Ok && result.Target is not null)
        {
            if (request.PersistCredentials)
            {
                _credentials.Save(request.Hostname, request.Username, request.Password);
                result.Target.CredentialsStored = true;
                _logs.Write($"Identifiants stockés (DPAPI) pour {request.Hostname}");
            }
            else
            {
                result.Target.CredentialsStored = false;
            }

            _preparedTargets.Upsert(result.Target);

            // Ensure hostname is in DnsTargets farm list
            var cfg = GetConfig();
            if (!cfg.DnsTargets.Any(t => t.Equals(request.Hostname.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                cfg.DnsTargets.Add(request.Hostname.Trim());
                SetConfig(cfg);
            }

            _logs.Write($"Cible préparée: {request.Hostname} (identité={result.Target.HubIdentityGranted})");
        }
        else
        {
            _logs.Write($"Préparation échouée pour {request.Hostname}: {result.Message}");
        }

        // Clear password from request object if caller reuses it (defensive)
        request.Password = "";
        return result;
    }

    public IReadOnlyList<PreparedTargetInfo> GetPreparedTargets()
    {
        var list = _preparedTargets.List().Select(t =>
        {
            var meta = _credentials.GetMeta(t.Hostname);
            t.CredentialsStored = meta is not null;
            if (meta is not null && string.IsNullOrWhiteSpace(t.Username))
                t.Username = meta.Username;
            return t;
        }).ToList();
        return list;
    }

    public bool ClearTargetCredentials(string hostname)
    {
        _credentials.Delete(hostname);
        var existing = _preparedTargets.Get(hostname);
        if (existing is not null)
        {
            existing.CredentialsStored = false;
            _preparedTargets.Upsert(existing);
        }

        _logs.Write($"Identifiants effacés pour {hostname}");
        return true;
    }

    private static void Normalize(AppConfig config)
    {
        config.ChunkSize = Math.Clamp(config.ChunkSize, 1, 5000);
        config.MaxParallelTargets = Math.Clamp(config.MaxParallelTargets, 1, 8);
        config.ScheduledHour = Math.Clamp(config.ScheduledHour, 0, 23);
        config.ScheduledMinute = Math.Clamp(config.ScheduledMinute, 0, 59);
        if (string.IsNullOrWhiteSpace(config.PolicyNamePrefix))
            config.PolicyNamePrefix = "DnsBlocker_";
        if (!config.PolicyAction.Equals("IGNORE", StringComparison.OrdinalIgnoreCase))
            config.PolicyAction = "DENY";
        config.DnsTargets = config.DnsTargets
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.UpdateFeedUrl = (config.UpdateFeedUrl ?? "").Trim();
    }

    private static AppConfig Clone(AppConfig c) => new()
    {
        BlocklistsUrl = c.BlocklistsUrl,
        WhitelistsUrl = c.WhitelistsUrl,
        RegexUrl = c.RegexUrl,
        ExtraBlocklistUrls = [.. c.ExtraBlocklistUrls],
        ExtraWhitelistUrls = [.. c.ExtraWhitelistUrls],
        CuratedYoutubeAds = c.CuratedYoutubeAds,
        CuratedOisdBig = c.CuratedOisdBig,
        CuratedOisdNsfw = c.CuratedOisdNsfw,
        CuratedChadmayfieldPorn = c.CuratedChadmayfieldPorn,
        DnsTargets = [.. c.DnsTargets],
        PolicyAction = c.PolicyAction,
        ChunkSize = c.ChunkSize,
        MaxParallelTargets = c.MaxParallelTargets,
        ScheduledHour = c.ScheduledHour,
        ScheduledMinute = c.ScheduledMinute,
        EnableStatsCollection = c.EnableStatsCollection,
        PolicyNamePrefix = c.PolicyNamePrefix,
        UpdateFeedUrl = c.UpdateFeedUrl,
        CheckUpdatesOnStartup = c.CheckUpdatesOnStartup,
        AutoInstallUpdates = c.AutoInstallUpdates,
        SetupWizardCompleted = c.SetupWizardCompleted,
        UiLanguage = c.UiLanguage
    };
}
