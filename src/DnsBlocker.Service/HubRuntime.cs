using System.Text.Json;
using DnsBlocker.Core.Config;
using DnsBlocker.Core.Dns;
using DnsBlocker.Core.Logging;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Net;
using DnsBlocker.Core.Paths;
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
        var priorAction = _config.PolicyAction;
        Normalize(_config);
        // Persist IGNORE migration (old installs saved DENY; MS blackhole + Vincent repro need IGNORE).
        var targetsDefaulted = LocalDnsTarget.TryEnsureDefaultTargets(_config);
        if (targetsDefaulted
            || !string.Equals(priorAction, _config.PolicyAction, StringComparison.OrdinalIgnoreCase))
        {
            _configStore.Save(_config);
            if (targetsDefaulted)
                _logs.Write($"Local DNS Server detected — DnsTargets defaulted to: {string.Join(", ", _config.DnsTargets)}");
            if (!string.Equals(priorAction, _config.PolicyAction, StringComparison.OrdinalIgnoreCase))
                _logs.Write($"PolicyAction migrated {priorAction} → {_config.PolicyAction} (blocklist QRP uses IGNORE).");
        }
        _lastPolicyProbe = TryLoadPersistedProbe();
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

        // Succeeded ≠ live Enabled policies — always probe Get-DnsServerQueryResolutionPolicy after sync.
        // If Enabled=0 or Resolve-DnsName still returns public IPs, flip the target to Failed.
        try
        {
            var probe = await GetTargetPolicyStatusAsync(cancellationToken).ConfigureAwait(false);
            var mutated = false;
            foreach (var row in probe.Targets)
            {
                _logs.Write(row.ProbeOk
                    ? $"QRP probe after sync: {row.Target} — installed={row.LivePolicyCount}, enabled={row.LiveEnabledCount}, sync={row.LastSyncPolicyCount} → {row.AppliedLabel}"
                    : $"QRP probe after sync FAIL: {row.Target} — {row.ProbeError}");

                var targetResult = result.Targets.FirstOrDefault(t =>
                    string.Equals(t.Target, row.Target, StringComparison.OrdinalIgnoreCase));
                if (targetResult is null || targetResult.Status != TargetSyncStatus.Succeeded)
                    continue;

                if (!row.ProbeOk || row.LiveEnabledCount == 0 || row.LiveEmptyFqdnCount > 0 || !row.AppliedOk)
                {
                    targetResult.Status = TargetSyncStatus.Failed;
                    targetResult.Error =
                        $"Post-sync QRP probe: installed={row.LivePolicyCount}, enabled={row.LiveEnabledCount}, " +
                        $"emptyFqdn={row.LiveEmptyFqdnCount}, sync={row.LastSyncPolicyCount} ({row.AppliedLabel}). " +
                        "Enabled count must be > 0, FQDN criteria non-empty, and match sync — blocking is not effective.";
                    mutated = true;
                    _logs.Write($"Target FAIL after probe: {row.Target} — {targetResult.Error}");
                    continue;
                }

                // Auto Test blocking (beeg.com) — same check Vincent runs in CMD.
                try
                {
                    var sample = PreferPostSyncBlockSample(row.Target);
                    var inList = _state.SearchDomains(sample, 20)
                        .Any(d => d.Equals(sample, StringComparison.OrdinalIgnoreCase)
                                  || d.Equals("*." + sample, StringComparison.OrdinalIgnoreCase));
                    var block = await _stats.TestBlockAsync(row.Target, sample, inList, cancellationToken)
                        .ConfigureAwait(false);
                    _logs.Write(
                        $"Auto block test after sync: {sample} → {row.Target}: blocked={block.AppearsBlocked} " +
                        $"({block.ResolutionSummary ?? block.Error ?? "n/a"})");

                    if (inList && block.ProbeOk && !block.AppearsBlocked)
                    {
                        targetResult.Status = TargetSyncStatus.Failed;
                        targetResult.Error =
                            $"Post-sync Resolve-DnsName {sample} still returns public IPs via {row.Target}: " +
                            $"{block.ResolutionSummary}. Sync must not report Succeeded when blocking fails.";
                        mutated = true;
                        _logs.Write($"Target FAIL after block test: {row.Target} — {targetResult.Error}");
                    }
                }
                catch (Exception blockEx)
                {
                    _logs.Write($"Auto block test after sync error ({row.Target}): {blockEx.Message}");
                }
            }

            if (mutated)
            {
                result.FinishedAt = DateTimeOffset.UtcNow;
                _state.SaveSyncResult(result);
            }
        }
        catch (Exception ex)
        {
            _logs.Write($"QRP probe after sync error: {ex.Message}");
        }

        return result;
    }

    private static string PreferPostSyncBlockSample(string _) => "beeg.com";

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
                var emptyFqdn = policies.Count(p => p.FqdnCriterionLength <= 0);
                var row = new TargetPolicyStatus
                {
                    Target = target,
                    IsLocal = LocalDnsTarget.IsLocalTarget(target),
                    LastSyncStatus = sync?.Status ?? TargetSyncStatus.Idle,
                    LastSyncFinishedAt = sync?.FinishedAt,
                    LastSyncError = sync?.Error,
                    LastSyncPolicyCount = sync?.PolicyCount ?? 0,
                    LivePolicyCount = policies.Count,
                    LiveEnabledCount = enabled,
                    LiveEmptyFqdnCount = emptyFqdn,
                    LivePolicyNames = policies.Select(p => p.Name).ToList(),
                    ProbeOk = true,
                    CheckedAt = checkedAt
                };
                results.Add(row);
                _logs.Write(
                    $"Policy probe OK: {target} — {policies.Count} installée(s), {enabled} activée(s), " +
                    $"emptyFqdn={emptyFqdn}" +
                    $" (sync: {row.LastSyncStatus}, {row.LastSyncPolicyCount}) → {row.AppliedLabel}");
                if (emptyFqdn > 0)
                {
                    _logs.Write(
                        $"WARNING: {emptyFqdn} QRP policy(ies) on {target} have empty FQDN criteria " +
                        "(Enabled≠match — beeg.com can still resolve).");
                }
            }
            catch (Exception ex)
            {
                var row = new TargetPolicyStatus
                {
                    Target = target,
                    IsLocal = LocalDnsTarget.IsLocalTarget(target),
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
        TryPersistProbe(result);

        return result;
    }

    private static TargetPolicyStatusResult? TryLoadPersistedProbe()
    {
        try
        {
            var path = AppPaths.LastPolicyProbePath;
            if (!File.Exists(path))
                return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TargetPolicyStatusResult>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private void TryPersistProbe(TargetPolicyStatusResult result)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.LastPolicyProbePath, json);
        }
        catch (Exception ex)
        {
            _logs.Write($"Could not persist QRP probe: {ex.Message}");
        }
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

    /// <summary>
    /// Detect local DNS Server, add preferred FQDN to DnsTargets, prepare via local PowerShell (no WinRM).
    /// </summary>
    public async Task<EnsureLocalDnsTargetResult> EnsureLocalDnsTargetAsync(
        CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        if (!LocalDnsTarget.IsDnsServerRunning())
        {
            return new EnsureLocalDnsTargetResult
            {
                Ok = false,
                DnsServerDetected = false,
                Message = "No local DNS Server service detected (feature/service DNS not running).",
                Log = ["DNS service not running or not installed on this machine."]
            };
        }

        var hostname = LocalDnsTarget.GetPreferredLocalName();
        log.Add($"Local DNS detected — preferred name: {hostname}");
        _logs.Write($"Ensure local DNS target → {hostname}");

        var prep = await _targetPrep.PrepareLocalAsync(hostname, cancellationToken).ConfigureAwait(false);
        log.AddRange(prep.Log);

        var cfg = GetConfig();
        var before = cfg.DnsTargets.ToList();
        LocalDnsTarget.EnsureInTargets(cfg);
        var added = !before.Any(t => t.Equals(hostname, StringComparison.OrdinalIgnoreCase));
        SetConfig(cfg);

        if (prep.Ok && prep.Target is not null)
        {
            prep.Target.Hostname = hostname;
            _preparedTargets.Upsert(prep.Target);
        }
        else if (prep.Ok)
        {
            _preparedTargets.Upsert(new PreparedTargetInfo
            {
                Hostname = hostname,
                Prepared = true,
                PreparedAtUtc = DateTimeOffset.UtcNow,
                LastProbeMessage = prep.Message,
                HubIdentityGranted = "local"
            });
        }

        var ok = prep.Ok;
        var message = ok
            ? (added
                ? $"Local DNS target added: {hostname} (prepared without WinRM)."
                : $"Local DNS target ready: {hostname} (already in farm; prepared without WinRM).")
            : $"Local DNS target '{hostname}' added to farm, but prepare failed: {prep.Message}";

        // Still OK for "add" if we at least put it in the farm — prepare may fail under non-elevated tests.
        // Prefer prep success for Ok flag so UI can show the probe result.
        _logs.Write(message);
        return new EnsureLocalDnsTargetResult
        {
            Ok = ok,
            DnsServerDetected = true,
            Hostname = hostname,
            Message = message,
            Log = log,
            AddedToFarm = added || before.All(SetupWizardGate.IsLocalOnlyTarget),
            Prepared = prep.Ok
        };
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
        config.ChunkSize = Math.Clamp(config.ChunkSize, 1, 200);
        // Cap at 2 — never fan out farm-wide in parallel on a DC hub.
        config.MaxParallelTargets = Math.Clamp(config.MaxParallelTargets, 1, 2);
        config.ScheduledHour = Math.Clamp(config.ScheduledHour, 0, 23);
        config.ScheduledMinute = Math.Clamp(config.ScheduledMinute, 0, 59);
        if (string.IsNullOrWhiteSpace(config.PolicyNamePrefix))
            config.PolicyNamePrefix = "DnsBlocker_";
        // Microsoft blackhole for blocklists is IGNORE (drop). DENY=REFUSED can let stubs
        // failover; windc04 also had Enabled Deny policies that never matched. Normalize to IGNORE.
        if (!config.PolicyAction.Equals("IGNORE", StringComparison.OrdinalIgnoreCase)
            && !config.PolicyAction.Equals("DENY", StringComparison.OrdinalIgnoreCase))
            config.PolicyAction = "IGNORE";
        if (config.PolicyAction.Equals("DENY", StringComparison.OrdinalIgnoreCase))
            config.PolicyAction = "IGNORE";
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
        MaxDomains = c.MaxDomains,
        PolicyApplyThrottleMs = c.PolicyApplyThrottleMs,
        PolicyApplyGcEvery = c.PolicyApplyGcEvery,
        MaxParallelTargets = c.MaxParallelTargets,
        RestartDnsAfterPolicySync = c.RestartDnsAfterPolicySync,
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
