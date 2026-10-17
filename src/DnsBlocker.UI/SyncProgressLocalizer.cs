using DnsBlocker.Core.Sync;
using DnsBlocker.UI.Localization;

namespace DnsBlocker.UI;

/// <summary>Maps service <see cref="SyncProgressUpdate"/> phases to FR/EN Action Log / Busy strings.</summary>
internal static class SyncProgressLocalizer
{
    public static string Format(SyncProgressUpdate p) =>
        p.Phase switch
        {
            SyncProgressPhase.Starting => Loc.F("Sync.Progress.Starting", p.Total),
            SyncProgressPhase.DownloadLol => Loc.F(
                "Sync.Progress.DownloadLol",
                LolKind(p.Detail),
                SyncProgressUpdate.ShortUrl(p.Url)),
            SyncProgressPhase.DownloadSource => Loc.F(
                "Sync.Progress.DownloadSource",
                p.Current,
                p.Total,
                SyncProgressUpdate.ShortUrl(p.Url),
                p.Added,
                p.Domains),
            SyncProgressPhase.MergeWhitelist => Loc.F("Sync.Progress.Merge", p.Domains, p.Total),
            SyncProgressPhase.StoreSqlite => Loc.F("Sync.Progress.Store", p.Domains),
            SyncProgressPhase.ChunkPolicies => Loc.F("Sync.Progress.Chunk", p.Domains, p.Total),
            SyncProgressPhase.ClassifyTargets => Loc.F("Sync.Progress.Classify", p.Total),
            SyncProgressPhase.PrioritizeTargets => Loc.F(
                "Sync.Progress.Prioritize", p.Current, p.Total, p.Detail ?? ""),
            SyncProgressPhase.ApplyTarget => string.IsNullOrWhiteSpace(p.Detail)
                ? Loc.F("Sync.Progress.ApplyTarget", p.Target ?? "?", p.Total)
                : Loc.F("Sync.Progress.ApplyTargetWhy", p.Target ?? "?", p.Total, p.Detail),
            SyncProgressPhase.ApplyProgress => Loc.F(
                "Sync.Progress.ApplyProgress", p.Target ?? "?", p.Current, p.Total),
            SyncProgressPhase.ApplyDone => p.Success
                ? Loc.F("Sync.Progress.ApplyDoneOk", p.Target ?? "?", p.Total)
                : Loc.F("Sync.Progress.ApplyDoneFail", p.Target ?? "?", p.Detail ?? ""),
            SyncProgressPhase.DeferTarget => Loc.F(
                "Sync.Progress.DeferTarget", p.Target ?? "?", p.Total, p.Detail ?? ""),
            SyncProgressPhase.VerifyProbe => Loc.T("Sync.Progress.Verify"),
            SyncProgressPhase.BlockTest => Loc.F(
                "Sync.Progress.BlockTest", p.Url ?? "?", p.Target ?? "?"),
            SyncProgressPhase.Done => p.Success
                ? Loc.F("Sync.Progress.DoneOk", p.Domains, p.Total)
                : Loc.F("Sync.Progress.DoneFail", p.Domains, p.Total),
            SyncProgressPhase.Error => Loc.F("Sync.Progress.Error", p.Detail ?? p.Message),
            _ => p.Message
        };

    public static string BusyHeader(SyncProgressUpdate p) =>
        Loc.F("Main.Busy.SyncDetail", Format(p));

    private static string LolKind(string? kind) =>
        string.Equals(kind, "whitelist", StringComparison.OrdinalIgnoreCase)
            ? Loc.T("Sync.Progress.LolWhitelist")
            : Loc.T("Sync.Progress.LolBlocklist");
}
