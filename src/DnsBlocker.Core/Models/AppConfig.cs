using DnsBlocker.Core.Updates;

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

    /// <summary>
    /// DENY (REFUSED) or IGNORE (drop). Default IGNORE matches Microsoft blackhole examples
    /// and reduces stub failover to a secondary public DNS after REFUSED.
    /// </summary>
    public string PolicyAction { get; set; } = "IGNORE";

    /// <summary>
    /// Max domains per QRP policy (further limited by FQDN criterion char budget ~8k).
    /// Default 200 — 1000-domain chunks produced ~19k-char criteria that Add could accept
    /// while leaving empty/ineffective FQDN criteria (Succeeded ≠ block).
    /// </summary>
    public int ChunkSize { get; set; } = 200;

    public int MaxParallelTargets { get; set; } = 2;

    /// <summary>
    /// After replacing QRP policies, restart the Windows DNS Server service so criteria
    /// are loaded into the query path (remote/local Adds can otherwise leave policies
    /// present but inert until Restart-Service DNS).
    /// </summary>
    public bool RestartDnsAfterPolicySync { get; set; } = true;

    /// <summary>Daily sync hour (local time), 0-23.</summary>
    public int ScheduledHour { get; set; } = 2;

    public int ScheduledMinute { get; set; } = 0;

    public bool EnableStatsCollection { get; set; } = false;

    public string PolicyNamePrefix { get; set; } = "DnsBlocker_";

    /// <summary>
    /// URL of version.json feed for application updates.
    /// Defaults to the public GitHub Releases latest asset; clear to disable checks.
    /// </summary>
    public string UpdateFeedUrl { get; set; } = UpdateFeedUrls.DefaultPublicFeedUrl;

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

    /// <summary>
    /// UI language override stored in ProgramData config.json.
    /// Empty / "auto" = follow OS UI culture (fr* → French, otherwise English).
    /// Explicit: "en" or "fr".
    /// </summary>
    public string UiLanguage { get; set; } = "auto";
}
