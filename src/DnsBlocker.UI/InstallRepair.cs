using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DnsBlocker.Core.Updates;

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

    public sealed record RepairResult(bool Ok, string MessageFr, string? LogPath);

    public static async Task<RepairResult> RepairAsync(
        string installRoot,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report("Vérification des binaires…");
        var svcExe = Path.Combine(installRoot, "DnsBlocker.Service.exe");
        var uiExe = Path.Combine(installRoot, "UI", "DnsBlocker.UI.exe");

        if (File.Exists(svcExe) && File.Exists(uiExe))
        {
            progress?.Report("Binaires présents — enregistrement / démarrage du service…");
            var reg = InstallHealth.TryRegisterService(installRoot);
            if (reg is not null)
                return new RepairResult(false, reg, null);

            var start = InstallHealth.TryStartService();
            if (start is not null)
                return new RepairResult(false, "Service enregistré mais démarrage échoué : " + start, null);

            return new RepairResult(true, "Service enregistré et démarré.", null);
        }

        progress?.Report("Installation incomplète détectée — téléchargement du MSI de réparation…");
        string msiPath;
        string? expectedSha = null;
        try
        {
            (msiPath, expectedSha) = await DownloadLatestMsiAsync(progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new RepairResult(false,
                "Téléchargement du MSI impossible : " + ex.Message +
                "\nTéléchargez DnsBlocker-*.msi depuis GitHub Releases, puis relancez l’assistant.",
                null);
        }

        if (!string.IsNullOrWhiteSpace(expectedSha))
        {
            progress?.Report("Vérification SHA-256…");
            var actual = await ComputeSha256Async(msiPath, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                return new RepairResult(false,
                    "SHA-256 du MSI incorrect (fichier corrompu). actual=" + actual, null);
            }
        }

        var log = Path.Combine(Path.GetTempPath(),
            "dnsblocker-repair-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        progress?.Report("Installation MSI (UAC éventuel)…");
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
                return new RepairResult(false, "UAC annulé — réparation MSI non lancée.", log);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode != 0)
            {
                return new RepairResult(false,
                    "msiexec a échoué (code " + p.ExitCode + "). Consultez le journal : " + log, log);
            }
        }
        catch (Exception ex)
        {
            return new RepairResult(false, "Échec msiexec : " + ex.Message, log);
        }

        progress?.Report("Contrôle post-installation…");
        await Task.Delay(1500, ct).ConfigureAwait(false);
        var report = InstallHealth.Run();
        if (!File.Exists(Path.Combine(report.InstallRoot, "DnsBlocker.Service.exe")))
        {
            return new RepairResult(false,
                "Après MSI, le binaire service est toujours absent. Journal : " + log, log);
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
                return new RepairResult(false, "MSI OK mais démarrage service : " + start, log);
        }

        return new RepairResult(true, "Réparation terminée. Journal : " + log, log);
    }

    private static async Task<(string Path, string? Sha256)> DownloadLatestMsiAsync(
        IProgress<string>? progress,
        CancellationToken ct)
    {
        // Prefer version.json from the same major feed we ship
        var version = AppVersion.Current;
        var feedUrl =
            "https://github.com/vincenttisseront/WinDNS-Blocker/releases/download/v" + version +
            "/version.json";
        string? msiUrl = null;
        string? sha = null;

        try
        {
            progress?.Report("Lecture du flux version.json…");
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

        msiUrl ??= "https://github.com/vincenttisseront/WinDNS-Blocker/releases/download/v" + version +
                   "/DnsBlocker-" + version + ".msi";

        var dest = Path.Combine(Path.GetTempPath(), "DnsBlocker-" + version + ".msi");
        progress?.Report("Téléchargement : " + msiUrl);
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
