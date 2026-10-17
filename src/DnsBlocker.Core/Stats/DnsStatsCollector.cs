using System.Diagnostics.Tracing;
using DnsBlocker.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnsBlocker.Core.Stats;

/// <summary>
/// Orchestrates hub-local ETW (best-effort) plus remote DNS analytical log ingest from farm targets.
/// </summary>
public sealed class DnsStatsCollector : IDisposable
{
    public const string DnsServerProviderName = "Microsoft-Windows-DNSServer";

    private readonly StateStore _state;
    private readonly DnsAnalyticalLogClient _analytical;
    private readonly ILogger<DnsStatsCollector> _logger;
    private EventListener? _listener;
    private bool _enabled;
    private string _message = "Stats collection is disabled.";
    private readonly List<AnalyticalLogStatus> _lastTargetStatus = [];
    private readonly object _gate = new();

    public DnsStatsCollector(
        StateStore state,
        DnsAnalyticalLogClient analytical,
        ILogger<DnsStatsCollector>? logger = null)
    {
        _state = state;
        _analytical = analytical;
        _logger = logger ?? NullLogger<DnsStatsCollector>.Instance;
    }

    public bool IsEnabled
    {
        get { lock (_gate) return _enabled; }
    }

    public string Message
    {
        get { lock (_gate) return _message; }
    }

    public IReadOnlyList<AnalyticalLogStatus> LastTargetStatus
    {
        get { lock (_gate) return _lastTargetStatus.ToList(); }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_enabled && _listener is not null)
                return;

            _enabled = true;
            _message = "Stats collection on — ingesting remote DNS analytical logs from configured targets.";

            if (!OperatingSystem.IsWindows())
            {
                _message = "ETW/analytical stats require Windows; remote ingest will no-op on this host.";
                return;
            }

            try
            {
                _listener ??= new DnsEventListener(_state, _logger);
                _logger.LogInformation("DNS stats collector started (local ETW + remote analytical ingest)");
            }
            catch (Exception ex)
            {
                // Local ETW is optional when DNS role is on another host.
                _logger.LogDebug(ex, "Local ETW listener unavailable (expected when hub ≠ DNS role)");
                _message =
                    "Remote analytical ingest active. Local ETW unavailable on this hub " +
                    "(normal when DNS runs on another server, e.g. windc05).";
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _listener?.Dispose();
            _listener = null;
            _enabled = false;
            _message = "Stats collection is disabled.";
            _lastTargetStatus.Clear();
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Enables analytical logging on each DNS target and turns collection on.
    /// </summary>
    public async Task<EnableStatsCollectionResult> EnableOnTargetsAsync(
        IReadOnlyList<string> targets,
        CancellationToken cancellationToken = default)
    {
        Start();
        var results = new List<AnalyticalLogStatus>();
        foreach (var target in targets.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await _analytical.EnsureAnalyticalLoggingAsync(target.Trim(), cancellationToken)
                .ConfigureAwait(false);
            _state.UpsertStatsIngestMeta(target.Trim(), status.AnalyticalEnabled, status.Message, 0, null);
            results.Add(status);
        }

        lock (_gate)
        {
            _lastTargetStatus.Clear();
            _lastTargetStatus.AddRange(results);
            var enabledCount = results.Count(r => r.AnalyticalEnabled);
            _message = results.Count == 0
                ? "No DNS targets configured — add farm hosts (e.g. windc05.ar-systems.fr)."
                : $"Collection on — analytical enabled on {enabledCount}/{results.Count} target(s). " +
                  "Clients must query those DNS servers.";
        }

        return new EnableStatsCollectionResult
        {
            StatsEnabled = true,
            Message = Message,
            Targets = results
        };
    }

    /// <summary>
    /// Pulls new analytical events from each target into <c>query_events</c>.
    /// </summary>
    public async Task<IReadOnlyList<AnalyticalLogStatus>> PollTargetsAsync(
        IReadOnlyList<string> targets,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return [];

        var results = new List<AnalyticalLogStatus>();
        foreach (var raw in targets.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = raw.Trim();
            try
            {
                var meta = _state.GetStatsIngestMeta(target);
                // Overlap 2s to avoid missing edge events; InsertQueryEvent is append-only.
                DateTimeOffset? since = meta?.LastEventUtc?.AddSeconds(-2);

                var events = await _analytical
                    .FetchQueryEventsAsync(target, since, maxEvents: 500, cancellationToken)
                    .ConfigureAwait(false);

                var ingested = 0;
                DateTimeOffset? maxTs = meta?.LastEventUtc;
                foreach (var evt in events.OrderBy(e => e.TimestampUtc))
                {
                    if (meta?.LastEventUtc is not null && evt.TimestampUtc <= meta.LastEventUtc
                        && meta.LastRecordId is not null && evt.RecordId is not null
                        && evt.RecordId <= meta.LastRecordId)
                        continue;

                    var blocked = _state.IsBlocked(evt.QueryName);
                    _state.InsertQueryEvent(evt.TimestampUtc, evt.ClientIp, evt.QueryName, blocked);
                    ingested++;
                    if (maxTs is null || evt.TimestampUtc > maxTs)
                        maxTs = evt.TimestampUtc;
                }

                var lastRecord = events.Count > 0
                    ? events.Max(e => e.RecordId ?? 0)
                    : meta?.LastRecordId;

                _state.UpsertStatsIngestMeta(
                    target,
                    analyticalEnabled: true,
                    lastError: null,
                    lastEventCount: ingested,
                    lastEventUtc: maxTs,
                    lastRecordId: lastRecord);

                results.Add(new AnalyticalLogStatus
                {
                    Target = target,
                    Ok = true,
                    AnalyticalEnabled = true,
                    EventsIngested = ingested,
                    LastEventUtc = maxTs,
                    Message = ingested > 0
                        ? $"Ingested {ingested} event(s)."
                        : "No new analytical events (logging on, but no queries seen — check client DNS)."
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Analytical ingest failed for {Target}", target);
                _state.UpsertStatsIngestMeta(target, analyticalEnabled: false, lastError: ex.Message, lastEventCount: 0, lastEventUtc: null);
                results.Add(new AnalyticalLogStatus
                {
                    Target = target,
                    Ok = false,
                    AnalyticalEnabled = false,
                    Message = ex.Message
                });
            }
        }

        lock (_gate)
        {
            _lastTargetStatus.Clear();
            _lastTargetStatus.AddRange(results);
            var ok = results.Count(r => r.Ok);
            var ingested = results.Sum(r => r.EventsIngested);
            _message =
                $"Collection on — last poll {ok}/{results.Count} target(s) ok, {ingested} new event(s). " +
                "Hub-local ETW alone cannot see remote DNS queries.";
        }

        return results;
    }

    public Task<AnalyticalLogStatus> ProbeAnalyticalAsync(
        string target,
        CancellationToken cancellationToken = default) =>
        _analytical.GetAnalyticalLoggingStatusAsync(target, cancellationToken);

    public Task<BlockTestResult> TestBlockAsync(
        string target,
        string domain,
        bool inBlocklist,
        CancellationToken cancellationToken = default) =>
        _analytical.TestBlockResolutionAsync(target, domain, inBlocklist, cancellationToken);

    /// <summary>
    /// Records a synthetic/demo query event (tests / manual ingest).
    /// </summary>
    public void RecordQuery(string queryName, string? clientIp, bool? blockedOverride = null)
    {
        var normalized = queryName.Trim('.').ToLowerInvariant();
        var blocked = blockedOverride ?? _state.IsBlocked(normalized);
        _state.InsertQueryEvent(DateTimeOffset.UtcNow, clientIp, normalized, blocked);
    }

    private sealed class DnsEventListener : EventListener
    {
        private readonly StateStore _state;
        private readonly ILogger _logger;

        public DnsEventListener(StateStore state, ILogger logger)
        {
            _state = state;
            _logger = logger;
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name.Contains("DNSServer", StringComparison.OrdinalIgnoreCase)
                || eventSource.Name.Contains("DNS-Server", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All);
                    _logger.LogInformation("Attached to ETW provider {Name}", eventSource.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not enable events for {Name}", eventSource.Name);
                }
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            try
            {
                string? query = null;
                string? client = null;
                if (eventData.Payload is null || eventData.PayloadNames is null)
                    return;

                for (var i = 0; i < eventData.Payload.Count && i < eventData.PayloadNames.Count; i++)
                {
                    var name = eventData.PayloadNames[i];
                    var value = eventData.Payload[i]?.ToString();
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    if (name.Contains("QNAME", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("QueryName", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Name", StringComparison.OrdinalIgnoreCase))
                        query = value;
                    else if (name.Contains("Source", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("Client", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("Remote", StringComparison.OrdinalIgnoreCase))
                        client = value;
                }

                if (string.IsNullOrWhiteSpace(query))
                    return;

                var normalized = query.Trim('.').ToLowerInvariant();
                var blocked = _state.IsBlocked(normalized);
                _state.InsertQueryEvent(DateTimeOffset.UtcNow, client, normalized, blocked);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to process DNS ETW event");
            }
        }
    }
}
