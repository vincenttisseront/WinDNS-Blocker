namespace DnsBlocker.Core.Stats;

public sealed class AnalyticalLogStatus
{
    public required string Target { get; init; }
    public bool Ok { get; init; }
    public bool AnalyticalEnabled { get; init; }
    public string? Message { get; init; }
    public int EventsIngested { get; init; }
    public DateTimeOffset? LastEventUtc { get; init; }
}

public sealed class EnableStatsCollectionResult
{
    public bool StatsEnabled { get; init; }
    public string Message { get; init; } = "";
    public List<AnalyticalLogStatus> Targets { get; init; } = [];
}

public sealed class DnsQueryEventDto
{
    public DateTimeOffset TimestampUtc { get; init; }
    public string QueryName { get; init; } = "";
    public string? ClientIp { get; init; }
    public long? RecordId { get; init; }
}

public sealed class BlockTestResult
{
    public required string Target { get; init; }
    public required string Domain { get; init; }
    public bool InBlocklist { get; init; }
    public bool ProbeOk { get; init; }
    public bool AppearsBlocked { get; init; }
    public string? ResolutionSummary { get; init; }
    public string? Error { get; init; }
    public string Guidance { get; init; } = "";
}

public sealed class BlockTestRequest
{
    public string? Domain { get; set; }
    public string? Target { get; set; }
}
