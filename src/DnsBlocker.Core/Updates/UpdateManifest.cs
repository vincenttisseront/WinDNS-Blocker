namespace DnsBlocker.Core.Updates;

/// <summary>
/// Matches a published <c>version.json</c> feed.
/// </summary>
public sealed class UpdateManifest
{
    public string Version { get; set; } = "";

    public string MsiUrl { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public string? ReleaseNotes { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class UpdateCheckResult
{
    public bool UpdateAvailable { get; set; }

    public string CurrentVersion { get; set; } = "";

    public UpdateManifest? Manifest { get; set; }

    public string? Message { get; set; }
}

public sealed class UpdateInstallResult
{
    public bool Started { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    public string? MsiPath { get; set; }

    public int? ExitCode { get; set; }
}
