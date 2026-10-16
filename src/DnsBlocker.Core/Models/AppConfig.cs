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

    /// <summary>kboghdady YouTube ads (pi-hole list). Re-fetched on scheduled sync / ApplyNow.</summary>
    public bool CuratedYoutubeAds { get; set; } = true;

    /// <summary>OISD big (domainswild). Default on — broad ads/trackers.</summary>
    public bool CuratedOisdBig { get; set; } = true;

    /// <summary>OISD NSFW (domainswild). Default on — family NSFW, lower FP than adult dumps.</summary>
    public bool CuratedOisdNsfw { get; set; } = true;

    /// <summary>Chadmayfield porn_all. Default off — large adult list; explicit opt-in.</summary>
    public bool CuratedChadmayfieldPorn { get; set; } = false;

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

    /// <summary>URL of version.json feed for application updates. Empty disables checks.</summary>
    public string UpdateFeedUrl { get; set; } = "";

    /// <summary>When true, the UI checks for updates on startup (if feed URL is set).</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    /// <summary>
    /// When true, the elevated service silently installs updates via msiexec /qn.
    /// When false, only notify (UI / logs).
    /// </summary>
    public bool AutoInstallUpdates { get; set; } = false;

    /// <summary>
    /// True after the user completes (or skips) the first-run setup wizard.
    /// Mirrored by ProgramData setup-complete.flag for install-worker coordination.
    /// </summary>
    public bool SetupWizardCompleted { get; set; } = false;
}
