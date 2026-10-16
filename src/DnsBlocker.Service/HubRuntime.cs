using DnsBlocker.Core.Config;
using DnsBlocker.Core.Logging;
using DnsBlocker.Core.Models;
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
    private readonly StateStore _state;
    private readonly FileLogBuffer _logs;
    private readonly DnsStatsCollector _stats;
    private readonly IUpdateChecker _updateChecker;
    private readonly UpdateInstaller _updateInstaller;
    private readonly object _configGate = new();
    private readonly object _updateGate = new();
    private AppConfig _config;
    private UpdateCheckResult? _lastUpdateCheck;

    public HubRuntime(
        ConfigStore configStore,
        SyncEngine syncEngine,
        StateStore state,
        FileLogBuffer logs,
        DnsStatsCollector stats,
        IUpdateChecker updateChecker,
        UpdateInstaller updateInstaller)
    {
        _configStore = configStore;
        _syncEngine = syncEngine;
        _state = state;
        _logs = logs;
        _stats = stats;
        _updateChecker = updateChecker;
        _updateInstaller = updateInstaller;
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
        return new ServiceStatus
        {
            IsSyncing = _syncEngine.IsSyncing,
            LastRun = _state.LoadLastSync(),
            NextScheduledUtc = nextScheduledUtc,
            ActiveDomainCount = _state.CountDomains(),
            RecentLogs = _logs.GetRecent(80).ToList(),
            StatsEnabled = _stats.IsEnabled,
            StatsMessage = _stats.Message
        };
    }

    public IReadOnlyList<string> SearchDomains(string? query, int limit) =>
        _state.SearchDomains(query, Math.Clamp(limit, 1, 1000));

    public StatsSnapshot GetStats(int hours)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        var to = DateTimeOffset.UtcNow;
        var from = to.AddHours(-hours);
        var snap = _state.GetStats(from, to);
        if (!_stats.IsEnabled && snap.Message is null)
        {
            return new StatsSnapshot
            {
                FromUtc = snap.FromUtc,
                ToUtc = snap.ToUtc,
                TotalQueries = snap.TotalQueries,
                BlockedQueries = snap.BlockedQueries,
                TopBlockedDomains = snap.TopBlockedDomains,
                TopClients = snap.TopClients,
                Message = _stats.Message
            };
        }

        return snap;
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

    /// <summary>
    /// Installs an update via msiexec. Prefer silent=true when called from the service
    /// (already LocalSystem). Do not stop the service manually — the MSI ServiceControl
    /// table should stop/restart DnsBlocker during the upgrade.
    /// </summary>
    public async Task<UpdateInstallResult> InstallUpdateAsync(
        UpdateManifest? manifest = null,
        bool silent = true,
        CancellationToken cancellationToken = default)
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
            _logs.Write($"msiexec started for {manifest.Version} — service may stop via MSI ServiceControl");
        else
            _logs.Write($"Update install failed: {result.Error}");
        return result;
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
        AutoInstallUpdates = c.AutoInstallUpdates
    };
}
