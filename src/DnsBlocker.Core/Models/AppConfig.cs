namespace DnsBlocker.Core.Models;

public sealed class AppConfig
{
    public string BlocklistsUrl { get; set; } =
        "https://raw.githubusercontent.com/stevejenkins/pi-hole-lists/main/blocklists.txt";

    public string WhitelistsUrl { get; set; } =
        "https://raw.githubusercontent.com/stevejenkins/pi-hole-lists/main/whitelists.txt";

    /// <summary>regex.txt is not supported by Windows DNS QRP; kept for UI disclosure only.</summary>
    public string RegexUrl { get; set; } =
        "https://raw.githubusercontent.com/stevejenkins/pi-hole-lists/main/regex.txt";

    public List<string> ExtraBlocklistUrls { get; set; } = [];

    public List<string> ExtraWhitelistUrls { get; set; } = [];

    public List<string> DnsTargets { get; set; } = ["localhost"];

    /// <summary>DENY or IGNORE</summary>
    public string PolicyAction { get; set; } = "DENY";

    /// <summary>
    /// Soft max domains per QRP policy before FQDN-budget packing. Prefer smaller values (≤250)
    /// to avoid CIM/criteria failures on Windows DNS.
    /// </summary>
    public int ChunkSize { get; set; } = 200;

    public int MaxParallelTargets { get; set; } = 2;

    /// <summary>Daily sync hour (local time), 0-23.</summary>
    public int ScheduledHour { get; set; } = 2;

    public int ScheduledMinute { get; set; } = 0;

    public bool EnableStatsCollection { get; set; } = false;

    public string PolicyNamePrefix { get; set; } = "DnsBlocker_";

    /// <summary>URL of version.json feed for application updates. Empty disables checks.</summary>
    public string UpdateFeedUrl { get; set; } = "";

    /// <summary>When true, the UI checks for updates on startup (if feed URL is set).</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>
    /// When true, the elevated service silently installs updates via msiexec /qn.
    /// When false, only notify (UI / logs).
    /// </summary>
    public bool AutoInstallUpdates { get; set; } = false;
}
