using DnsBlocker.Core.Updates;

namespace DnsBlocker.Core.Models;

public sealed class AppConfig
{
    /// <summary>
    /// Steve Jenkins pi-hole list-of-lists (multi-million hosts). Opt-in — leave blank and use
    /// curated OISD wildcards on domain controllers (see <see cref="MaxDomains"/>).
    /// </summary>
    public const string SteveJenkinsBlocklistsUrl =
        "https://raw.githubusercontent.com/stevejenkins/pi-hole-lists/main/blocklists.txt";

    public const string SteveJenkinsWhitelistsUrl =
        "https://raw.githubusercontent.com/stevejenkins/pi-hole-lists/main/whitelists.txt";

    /// <summary>
    /// List-of-lists URL. Default empty = curated-lists-only (recommended on DCs).
    /// Set to <see cref="SteveJenkinsBlocklistsUrl"/> to pull the full Steve Jenkins indexes.
    /// </summary>
    public string BlocklistsUrl { get; set; } = "";

    /// <summary>Whitelist list-of-lists URL. Empty skips LoL whitelist download.</summary>
    public string WhitelistsUrl { get; set; } = "";

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
    /// Default 150 — keeps criterion strings smaller and sync RSS lower on DCs.
    /// </summary>
    public int ChunkSize { get; set; } = 150;

    /// <summary>
    /// Hard cap on ingested domains (after whitelist, before QRP prepare).
    /// Default 400_000 keeps service working set under ~300 MB on a DC.
    /// Set to 0 for unlimited (not recommended on domain controllers).
    /// </summary>
    public int MaxDomains { get; set; } = 400_000;

    /// <summary>
    /// Pause (ms) every <see cref="PolicyApplyGcEvery"/> QRP Adds so CIM/DNS share the DC CPU.
    /// </summary>
    public int PolicyApplyThrottleMs { get; set; } = 15;

    /// <summary>
    /// Run <c>[GC]::Collect()</c> in the apply PowerShell session every N policy Adds (0 = never).
    /// </summary>
    public int PolicyApplyGcEvery { get; set; } = 25;

    /// <summary>
    /// Parallel DNS targets during apply. Default 1 — parallel apply doubles PowerShell/manifest
    /// memory on the hub (observed ~1.1 GB RSS crushing windc04 during 6M ingest).
    /// </summary>
    public int MaxParallelTargets { get; set; } = 1;

    /// <summary>
    /// How many already-populated DNS targets to refresh per sync after empty ones.
    /// Default 1 = au fil de l'eau (round-robin by oldest success). 0 = unlimited (legacy full fan-out).
    /// Empty / probe-failed targets are always applied in the same run.
    /// </summary>
    public int MaxPopulatedTargetsPerSync { get; set; } = 1;

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
