using System.Diagnostics.Tracing;
using DnsBlocker.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnsBlocker.Core.Stats;

/// <summary>
/// Best-effort ETW listener for Microsoft-Windows-DNS-Server analytical events.
/// When the provider is unavailable, the collector stays idle and reports a clear message.
/// </summary>
public sealed class DnsStatsCollector : IDisposable
{
    public const string DnsServerProviderName = "Microsoft-Windows-DNSServer";

    private readonly StateStore _state;
    private readonly ILogger<DnsStatsCollector> _logger;
    private EventListener? _listener;
    private bool _enabled;
    private string _message = "Stats collection is disabled.";

    public DnsStatsCollector(StateStore state, ILogger<DnsStatsCollector>? logger = null)
    {
        _state = state;
        _logger = logger ?? NullLogger<DnsStatsCollector>.Instance;
    }

    public bool IsEnabled => _enabled;
    public string Message => _message;

    public void Start()
    {
        if (_listener is not null)
            return;

        if (!OperatingSystem.IsWindows())
        {
            _enabled = false;
            _message = "ETW DNS stats require Windows Server with DNS analytical logging.";
            return;
        }

        try
        {
            _listener = new DnsEventListener(_state, _logger);
            _enabled = true;
            _message = "Collecting DNS analytical events (when the provider is enabled).";
            _logger.LogInformation("DNS stats collector started");
        }
        catch (Exception ex)
        {
            _enabled = false;
            _message = $"Unable to start ETW listener: {ex.Message}. Enable Microsoft-Windows-DNSServer/Analytical.";
            _logger.LogWarning(ex, "Failed to start DNS stats collector");
        }
    }

    public void Stop()
    {
        _listener?.Dispose();
        _listener = null;
        _enabled = false;
        _message = "Stats collection is disabled.";
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Records a synthetic/demo query event (also used by tests and when ETW is unavailable but manual ingest is needed).
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
                // Payload shapes vary by event id; extract query name / client when present.
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
