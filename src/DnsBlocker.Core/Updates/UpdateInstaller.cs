using System.Diagnostics;
using System.Security.Cryptography;

namespace DnsBlocker.Core.Updates;

/// <summary>
/// Downloads an MSI, verifies SHA-256, then launches msiexec.
/// <list type="bullet">
/// <item><description><c>silent=true</c> → <c>msiexec /i … /qn</c> (service / LocalSystem; no UI).</description></item>
/// <item><description><c>silent=false</c> → <c>msiexec /i … /qb</c> with <c>Verb=runas</c> so the interactive UI triggers UAC.</description></item>
/// </list>
/// File I/O, HTTP, hashing, and process start are injectable for unit tests.
/// </summary>
public sealed class UpdateInstaller
{
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
        _waitForExit = waitForExit;
    }

    public async Task<UpdateInstallResult> InstallAsync(
        UpdateManifest manifest,
        bool silent = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.MsiUrl))
            return Fail("Manifest MSI URL is empty.");
        if (string.IsNullOrWhiteSpace(manifest.Sha256))
            return Fail("Manifest SHA-256 is empty.");

        string? msiPath = null;
        try
        {
            msiPath = _files.GetTempFilePath(".msi");
            await using (var remote = await _http.GetStreamAsync(manifest.MsiUrl, cancellationToken).ConfigureAwait(false))
            await using (var local = _files.OpenWrite(msiPath))
            {
                await remote.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
            }

            var actualHash = await ComputeSha256HexAsync(msiPath, cancellationToken).ConfigureAwait(false);
            if (!HashesEqual(actualHash, manifest.Sha256))
            {
                _files.TryDelete(msiPath);
                return Fail($"SHA-256 mismatch (expected {manifest.Sha256}, got {actualHash}).");
            }

            var args = $"/i \"{msiPath}\" {(silent ? "/qn" : "/qb")}";
            var startInfo = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = args,
                UseShellExecute = !silent,
                // Interactive UI path: request elevation (UAC). Service path is already LocalSystem.
                Verb = silent ? "" : "runas",
                CreateNoWindow = silent,
                WindowStyle = silent ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
            };

            var exit = await _process.StartAsync(startInfo, _waitForExit, cancellationToken).ConfigureAwait(false);

            return new UpdateInstallResult
            {
                Started = true,
                Success = !_waitForExit || exit is 0,
                MsiPath = msiPath,
                ExitCode = exit,
                Error = _waitForExit && exit is not 0 and not null
                    ? $"msiexec exited with code {exit}."
                    : null
            };
        }
        catch (Exception ex)
        {
            if (msiPath is not null)
                _files.TryDelete(msiPath);
            return Fail(ex.Message);
        }
    }

    private async Task<string> ComputeSha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = _files.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static bool HashesEqual(string actualHex, string expected)
    {
        var normalizedExpected = expected.Trim().Replace("-", "", StringComparison.Ordinal);
        return string.Equals(actualHex, normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static UpdateInstallResult Fail(string error) =>
        new() { Started = false, Success = false, Error = error };
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
    /// <param name="waitForExit">When false, fire-and-forget (preferred for in-service upgrades).</param>
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
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Failed to start msiexec.");
        if (!waitForExit)
            return Task.FromResult<int?>(null);

        return WaitAsync(process, cancellationToken);
    }

    private static async Task<int?> WaitAsync(Process process, CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}
