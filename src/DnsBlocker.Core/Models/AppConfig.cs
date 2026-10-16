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

    public int ChunkSize { get; set; } = 1000;

    public int MaxParallelTargets { get; set; } = 2;

    /// <summary>Daily sync hour (local time), 0-23.</summary>
    public int ScheduledHour { get; set; } = 2;

    public int ScheduledMinute { get; set; } = 0;

    public bool EnableStatsCollection { get; set; } = false;

    public string PolicyNamePrefix { get; set; } = "DnsBlocker_";
}
