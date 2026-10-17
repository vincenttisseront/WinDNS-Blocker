using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DnsBlocker.Core.Updates;

/// <summary>
/// Downloads an MSI, verifies SHA-256, then launches msiexec after releasing file locks.
/// <list type="bullet">
/// <item><description><c>silent=true</c> → detached cmd: stop service + kill UI/Service, wait for exit, then <c>msiexec /qn</c>.</description></item>
/// <item><description><c>silent=false</c> → elevated cmd (UAC): same prep, then <c>msiexec /qb</c>; caller should <c>Shutdown()</c> the UI.</description></item>
/// </list>
/// Prep runs <b>before</b> msiexec so InstallFiles is not blocked by a live sync, DnsBlocker service, or WPF UI
/// (Restart Manager is disabled in the MSI). Never Kill/Exit the host from this class — callers own shutdown.
/// </summary>
public sealed class UpdateInstaller
{
    private static readonly Regex Sha256Hex = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly IUpdateFileSystem _files;
    private readonly IProcessLauncher _process;
    private readonly bool _waitForExit;

    public UpdateInstaller(
        HttpClient? httpClient = null,
        IUpdateFileSystem? files = null,
        IProcessLauncher? process = null,
        bool waitForExit = false)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DnsBlocker/1.0");
        _files = files ?? new DefaultUpdateFileSystem();
        _process = process ?? new DefaultProcessLauncher();
        // Service upgrades should not WaitForExit — msiexec stops this process via MSI ServiceControl.
        // Never WaitForExit from the in-app UI path either: the host must stay alive to show MessageBox errors.
        _waitForExit = waitForExit;
    }

    public async Task<UpdateInstallResult> InstallAsync(
        UpdateManifest manifest,
        bool silent = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.MsiUrl))
            return Fail("URL MSI du manifeste vide. / Manifest MSI URL is empty.");
        if (string.IsNullOrWhiteSpace(manifest.Sha256))
            return Fail("SHA-256 du manifeste vide. / Manifest SHA-256 is empty.");

        var expectedSha = NormalizeSha(manifest.Sha256);
        if (expectedSha is null)
        {
            return Fail(
                "SHA-256 du manifeste invalide (64 caractères hex attendus). / " +
                "Manifest SHA-256 is invalid (expected 64 hex characters).");
        }

        string? msiPath = null;
        try
        {
            msiPath = _files.GetTempFilePath(".msi");

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(manifest.MsiUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Fail("Délai dépassé lors du téléchargement MSI. / MSI download timed out.");
            }
            catch (HttpRequestException ex)
            {
                return Fail("Échec HTTP du téléchargement MSI. / MSI download HTTP failed. " + Truncate(ex.Message, 160));
            }
            catch (IOException ex)
            {
                return Fail("Erreur I/O lors du téléchargement MSI. / MSI download I/O error. " + Truncate(ex.Message, 160));
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    return Fail(
                        $"Téléchargement MSI refusé (HTTP {code}). / MSI download rejected (HTTP {code}).");
                }

                try
                {
                    await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await using var local = _files.OpenWrite(msiPath);
                    await remote.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    _files.TryDelete(msiPath);
                    return Fail("Échec HTTP pendant le téléchargement MSI. / HTTP failed while downloading MSI. " +
                                Truncate(ex.Message, 160));
                }
                catch (IOException ex)
                {
                    _files.TryDelete(msiPath);
                    return Fail("Erreur I/O pendant l'écriture du MSI. / I/O error writing MSI. " +
                                Truncate(ex.Message, 160));
                }
                catch (UnauthorizedAccessException ex)
                {
                    _files.TryDelete(msiPath);
                    return Fail("Accès refusé à l'écriture du MSI temporaire. / Access denied writing temp MSI. " +
                                Truncate(ex.Message, 120));
                }
            }

            string actualHash;
            try
            {
                actualHash = await ComputeSha256HexAsync(msiPath, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _files.TryDelete(msiPath);
                return Fail("Impossible de lire le MSI pour vérification SHA-256. / Cannot read MSI for SHA-256. " +
                            Truncate(ex.Message, 160));
            }
            catch (CryptographicException ex)
            {
                _files.TryDelete(msiPath);
                return Fail("Échec du calcul SHA-256. / SHA-256 computation failed. " + Truncate(ex.Message, 120));
            }

            if (!string.Equals(actualHash, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                _files.TryDelete(msiPath);
                return Fail(
                    $"SHA-256 incorrect (attendu {expectedSha}, reçu {actualHash}). / " +
                    $"SHA-256 mismatch (expected {expectedSha}, got {actualHash}).");
            }

            // /norestart: never reboot. Fire-and-forget; do not Kill/Exit host from here.
            // Both paths: stop service + kill related processes + wait for handles, THEN msiexec.
            // Silent: detach via start /b so the service process tree dying cannot abort msiexec.
            var startInfo = BuildMsiexecStartInfo(msiPath, silent);

            int? exit;
            try
            {
                exit = await _process.StartAsync(startInfo, _waitForExit, cancellationToken).ConfigureAwait(false);
            }
            catch (Win32Exception ex)
            {
                return Fail(
                    $"Impossible de démarrer msiexec (Win32 {ex.NativeErrorCode}). / " +
                    $"Failed to start msiexec (Win32 {ex.NativeErrorCode}). " + Truncate(ex.Message, 120));
            }
            catch (InvalidOperationException ex)
            {
                return Fail(
                    "Impossible de démarrer msiexec. / Failed to start msiexec. " + Truncate(ex.Message, 160));
            }

            return new UpdateInstallResult
            {
                Started = true,
                Success = !_waitForExit || exit is 0,
                MsiPath = msiPath,
                ExitCode = exit,
                Error = _waitForExit && exit is not 0 and not null
                    ? $"msiexec a quitté avec le code {exit}. / msiexec exited with code {exit}."
                    : null
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (msiPath is not null)
                _files.TryDelete(msiPath);
            throw;
        }
        catch (HttpRequestException ex)
        {
            if (msiPath is not null)
                _files.TryDelete(msiPath);
            return Fail("Échec HTTP de la mise à jour. / Update HTTP failed. " + Truncate(ex.Message, 160));
        }
        catch (IOException ex)
        {
            if (msiPath is not null)
                _files.TryDelete(msiPath);
            return Fail("Erreur I/O de la mise à jour. / Update I/O error. " + Truncate(ex.Message, 160));
        }
        catch (Exception ex)
        {
            if (msiPath is not null)
                _files.TryDelete(msiPath);
            return Fail("Échec de l'installation de la mise à jour. / Update install failed. " +
                        Truncate(ex.Message, 160));
        }
    }

    /// <summary>
    /// Builds the process that stops DnsBlocker (service + UI), waits for processes to exit,
    /// then launches msiexec. Interactive uses UAC (<c>runas</c>); silent detaches msiexec via <c>start /b</c>.
    /// </summary>
    internal static ProcessStartInfo BuildMsiexecStartInfo(string msiPath, bool silent)
    {
        // /norestart: never reboot. Quiet UI flags only differ.
        var msiexecArgs = $"/i \"{msiPath}\" {(silent ? "/qn" : "/qb")} /norestart";
        var prep = BuildPreUpgradeCmd(msiPath, msiexecArgs, detachMsiexec: silent);

        if (silent)
        {
            return new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/c " + prep,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
        }

        // Elevated helper: stop service requires admin; same UAC prompt as msiexec alone.
        return new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/c " + prep,
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };
    }

    /// <summary>
    /// Cmd script: brief grace for UI Shutdown → request service stop → force-kill leftovers →
    /// settle (handles/pipe) → msiexec (detached when <paramref name="detachMsiexec"/>).
    /// Matches <c>Recover-DnsBlocker.cmd</c> stop order; <c>sc stop</c> alone is not enough when sync
    /// holds PowerShell children, so <c>taskkill /F</c> always follows a short wait.
    /// </summary>
    internal static string BuildPreUpgradeCmd(string msiPath, string msiexecArgs, bool detachMsiexec)
    {
        _ = msiPath; // path already embedded in msiexecArgs
        // & chaining keeps one /c line (no nested batches / paren quoting issues).
        var msiexecLaunch = detachMsiexec
            ? "start \"DnsBlockerMsi\" /b msiexec.exe " + msiexecArgs
            : "msiexec.exe " + msiexecArgs;

        return
            // 1) Grace so interactive UI can begin Application.Shutdown() before force-kill.
            "ping -n 3 127.0.0.1 >nul & " +
            // 2) Ask SCM to stop (releases named pipe + cancels in-flight work when OnStop runs).
            "sc stop DnsBlocker >nul 2>&1 & " +
            "ping -n 4 127.0.0.1 >nul & " +
            // 3) Force-kill anything still holding Program Files\DnsBlocker locks.
            "taskkill /IM DnsBlocker.UI.exe /F >nul 2>&1 & " +
            "taskkill /IM DnsBlocker.Service.exe /F >nul 2>&1 & " +
            // 4) Extra settle so InstallFiles does not race dying process handles.
            "ping -n 5 127.0.0.1 >nul & " +
            // 5) Install
            msiexecLaunch;
    }

    private async Task<string> ComputeSha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = _files.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string? NormalizeSha(string expected)
    {
        var normalized = expected.Trim().Replace("-", "", StringComparison.Ordinal);
        return Sha256Hex.IsMatch(normalized) ? normalized : null;
    }

    private static UpdateInstallResult Fail(string error) =>
        new() { Started = false, Success = false, Error = error };

    private static string Truncate(string message, int max)
    {
        if (string.IsNullOrEmpty(message) || message.Length <= max)
            return message;
        return message[..max] + "…";
    }
}

public interface IUpdateFileSystem
{
    string GetTempFilePath(string extension);
    Stream OpenWrite(string path);
    Stream OpenRead(string path);
    void TryDelete(string path);
}

public interface IProcessLauncher
{
    /// <param name="waitForExit">When false, fire-and-forget (preferred for in-app / in-service upgrades).</param>
    Task<int?> StartAsync(ProcessStartInfo startInfo, bool waitForExit, CancellationToken cancellationToken);
}

public sealed class DefaultUpdateFileSystem : IUpdateFileSystem
{
    public string GetTempFilePath(string extension)
    {
        var name = "DnsBlocker-update-" + Guid.NewGuid().ToString("N") + extension;
        return Path.Combine(Path.GetTempPath(), name);
    }

    public Stream OpenWrite(string path) =>
        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}

public sealed class DefaultProcessLauncher : IProcessLauncher
{
    public Task<int?> StartAsync(ProcessStartInfo startInfo, bool waitForExit, CancellationToken cancellationToken)
    {
        // Start msiexec only — never Kill/Exit the host process (UI must stay open for MessageBox).
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception)
        {
            throw;
        }

        if (process is null)
            throw new InvalidOperationException("Process.Start returned null for msiexec.");

        if (!waitForExit)
        {
            // Detach: do not WaitForExit, do not Kill host or child from here.
            process.Dispose();
            return Task.FromResult<int?>(null);
        }

        return WaitAsync(process, cancellationToken);
    }

    private static async Task<int?> WaitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            process.Dispose();
        }
    }
}
