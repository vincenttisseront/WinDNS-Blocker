using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Paths;
using DnsBlocker.Core.Updates;

namespace DnsBlocker.Core.Ipc;

public sealed class IpcClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly int _connectTimeoutMs;

    public IpcClient(string? pipeName = null, int connectTimeoutMs = 3000)
    {
        _pipeName = pipeName ?? AppPaths.PipeName;
        _connectTimeoutMs = connectTimeoutMs;
    }

    public Task<ServiceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<ServiceStatus>(IpcProtocol.MethodGetStatus, null, cancellationToken);

    public Task<AppConfig> GetConfigAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<AppConfig>(IpcProtocol.MethodGetConfig, null, cancellationToken);

    public Task<AppConfig> SetConfigAsync(AppConfig config, CancellationToken cancellationToken = default) =>
        InvokeAsync<AppConfig>(IpcProtocol.MethodSetConfig, new SetConfigParams { Config = config }, cancellationToken);

    public Task<SyncRunResult> ApplyNowAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<SyncRunResult>(IpcProtocol.MethodApplyNow, null, cancellationToken);

    public Task<List<string>> SearchDomainsAsync(string? query, int limit = 100, CancellationToken cancellationToken = default) =>
        InvokeAsync<List<string>>(IpcProtocol.MethodSearchDomains, new SearchDomainsParams { Query = query, Limit = limit }, cancellationToken);

    public Task<StatsSnapshot> GetStatsAsync(int hours = 24, CancellationToken cancellationToken = default) =>
        InvokeAsync<StatsSnapshot>(IpcProtocol.MethodGetStats, new GetStatsParams { Hours = hours }, cancellationToken);

    public Task<AppVersionResult> GetAppVersionAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<AppVersionResult>(IpcProtocol.MethodGetAppVersion, null, cancellationToken);

    public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<UpdateCheckResult>(IpcProtocol.MethodCheckForUpdates, null, cancellationToken);

    public Task<UpdateInstallResult> InstallUpdateAsync(
        UpdateManifest? manifest = null,
        bool silent = true,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<UpdateInstallResult>(
            IpcProtocol.MethodInstallUpdate,
            new InstallUpdateParams { Manifest = manifest, Silent = silent },
            cancellationToken);

    /// <summary>Lightweight connectivity probe (GetAppVersion).</summary>
    public async Task<bool> TryPingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await GetAppVersionAsync(cancellationToken).ConfigureAwait(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<T> InvokeAsync<T>(string method, object? parameters, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.ConnectAsync(_connectTimeoutMs, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException(
                    $"Le service DnsBlocker ne répond pas (pipe '{_pipeName}', délai {_connectTimeoutMs} ms). " +
                    "Vérifiez que le service Windows « DnsBlocker » est démarré.",
                    ex);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"Impossible de se connecter au service DnsBlocker (pipe '{_pipeName}'): {ex.Message}. " +
                    "Le service est peut‑être arrêté, ou le pipe n'autorise pas votre compte.",
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    "Accès refusé au pipe IPC DnsBlocker. Le service tourne probablement en LocalSystem " +
                    "sans ACL pour les utilisateurs interactifs — redémarrez le service après mise à jour.",
                    ex);
            }

            var request = new IpcRequest
            {
                Method = method,
                Params = parameters is null ? null : JsonSerializer.SerializeToElement(parameters, IpcProtocol.JsonOptions)
            };

            var payload = JsonSerializer.Serialize(request, IpcProtocol.JsonOptions) + "\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            await pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("Réponse IPC vide du service DnsBlocker.");

            var response = JsonSerializer.Deserialize<IpcResponse>(line, IpcProtocol.JsonOptions)
                           ?? throw new InvalidOperationException("Réponse IPC invalide.");

            if (!response.Ok)
                throw new InvalidOperationException(response.Error ?? "Appel IPC échoué.");

            if (response.Result is null)
                throw new InvalidOperationException("Réponse IPC sans résultat.");

            return response.Result.Value.Deserialize<T>(IpcProtocol.JsonOptions)
                   ?? throw new InvalidOperationException("Désérialisation IPC impossible.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Erreur de communication avec le service DnsBlocker: " + ex.Message,
                ex);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
