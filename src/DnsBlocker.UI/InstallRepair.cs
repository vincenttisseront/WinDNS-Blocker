using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DnsBlocker.Core.Updates;
using DnsBlocker.UI.Localization;

namespace DnsBlocker.UI;

/// <summary>
/// GUI-driven repair: re-register service when binaries exist, otherwise download + msiexec.
/// </summary>
internal static class InstallRepair
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public sealed record RepairResult(bool Ok, string Message, string? LogPath);

    public static async Task<RepairResult> RepairAsync(
        string installRoot,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report(Loc.T("Repair.CheckBins"));
        var svcExe = Path.Combine(installRoot, "DnsBlocker.Service.exe");
        var uiExe = Path.Combine(installRoot, "UI", "DnsBlocker.UI.exe");

        if (File.Exists(svcExe) && File.Exists(uiExe))
        {
            progress?.Report(Loc.T("Repair.BinsPresent"));
            var reg = InstallHealth.TryRegisterService(installRoot);
            if (reg is not null)
                return new RepairResult(false, reg, null);

            var start = InstallHealth.TryStartService();
            if (start is not null)
                return new RepairResult(false, Loc.F("Repair.StartFail", start), null);

            return new RepairResult(true, Loc.T("Repair.RegisteredStarted"), null);
        }

        progress?.Report(Loc.T("Repair.SparseDownload"));
        string msiPath;
        string? expectedSha = null;
        try
        {
            (msiPath, expectedSha) = await DownloadLatestMsiAsync(progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new RepairResult(false, Loc.F("Repair.DownloadFail", ex.Message), null);
        }

        if (!string.IsNullOrWhiteSpace(expectedSha))
        {
            progress?.Report(Loc.T("Repair.ShaCheck"));
            var actual = await ComputeSha256Async(msiPath, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                return new RepairResult(false, Loc.F("Repair.ShaFail", actual), null);
            }
        }

        var log = Path.Combine(Path.GetTempPath(),
            "dnsblocker-repair-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        progress?.Report(Loc.T("Repair.Msiexec"));
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = "/i \"" + msiPath + "\" /l*v \"" + log + "\" /qb /norestart",
                UseShellExecute = true,
                Verb = "runas"
            };
            using var p = Process.Start(psi);
            if (p is null)
                return new RepairResult(false, Loc.T("Repair.UacCancel"), log);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0)
            {
                return new RepairResult(false, Loc.F("Repair.MsiexecFail", p.ExitCode, log), log);
            }
        }
        catch (Exception ex)
        {
            return new RepairResult(false, Loc.F("Repair.MsiexecError", ex.Message), log);
        }

        progress?.Report(Loc.T("Repair.PostCheck"));
        await Task.Delay(1500, ct).ConfigureAwait(false);
        var report = InstallHealth.Run();
        if (!File.Exists(Path.Combine(report.InstallRoot, "DnsBlocker.Service.exe")))
        {
            return new RepairResult(false, Loc.F("Repair.StillMissing", log), log);
        }

        if (!report.ServiceRegistered)
        {
            var reg = InstallHealth.TryRegisterService(report.InstallRoot);
            if (reg is not null)
                return new RepairResult(false, reg, log);
        }

        if (!report.ServiceRunning)
        {
            var start = InstallHealth.TryStartService();
            if (start is not null)
                return new RepairResult(false, Loc.F("Repair.MsiStartFail", start), log);
        }

        return new RepairResult(true, Loc.F("Repair.Done", log), log);
    }

    private static async Task<(string Path, string? Sha256)> DownloadLatestMsiAsync(
        IProgress<string>? progress,
        CancellationToken ct)
    {
        // Prefer stable latest feed so repair always pulls the newest published MSI
        var version = AppVersion.Current;
        var feedUrl = UpdateFeedUrls.DefaultPublicFeedUrl;
        string? msiUrl = null;
        string? sha = null;

        try
        {
            progress?.Report(Loc.T("Repair.ReadFeed"));
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(feedUrl, ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("msiUrl", out var u))
                msiUrl = u.GetString();
            if (doc.RootElement.TryGetProperty("sha256", out var s))
                sha = s.GetString();
            if (doc.RootElement.TryGetProperty("version", out var v) && v.GetString() is { } ver)
                version = ver;
        }
        catch
        {
            // fall through to constructed URL
        }

        msiUrl ??= UpdateFeedUrls.TagMsiUrl(version);

        var dest = Path.Combine(Path.GetTempPath(), "DnsBlocker-" + version + ".msi");
        progress?.Report(Loc.F("Repair.Downloading", msiUrl));
        await using (var remote = await Http.GetStreamAsync(msiUrl, ct).ConfigureAwait(false))
        await using (var local = File.Create(dest))
        {
            await remote.CopyToAsync(local, ct).ConfigureAwait(false);
        }

        return (dest, sha);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
