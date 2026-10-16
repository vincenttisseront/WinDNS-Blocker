using DnsBlocker.Core.Config;
using DnsBlocker.Core.Logging;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Stats;
using DnsBlocker.Core.Storage;
using DnsBlocker.Core.Sync;

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
    private readonly object _configGate = new();
    private AppConfig _config;

    public HubRuntime(
        ConfigStore configStore,
        SyncEngine syncEngine,
        StateStore state,
        FileLogBuffer logs,
        DnsStatsCollector stats)
    {
        _configStore = configStore;
        _syncEngine = syncEngine;
        _state = state;
        _logs = logs;
        _stats = stats;
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
    }

    private static AppConfig Clone(AppConfig c) => new()
    {
        BlocklistsUrl = c.BlocklistsUrl,
        WhitelistsUrl = c.WhitelistsUrl,
        RegexUrl = c.RegexUrl,
        ExtraBlocklistUrls = [.. c.ExtraBlocklistUrls],
        ExtraWhitelistUrls = [.. c.ExtraWhitelistUrls],
        DnsTargets = [.. c.DnsTargets],
        PolicyAction = c.PolicyAction,
        ChunkSize = c.ChunkSize,
        MaxParallelTargets = c.MaxParallelTargets,
        ScheduledHour = c.ScheduledHour,
        ScheduledMinute = c.ScheduledMinute,
        EnableStatsCollection = c.EnableStatsCollection,
        PolicyNamePrefix = c.PolicyNamePrefix
    };
}
