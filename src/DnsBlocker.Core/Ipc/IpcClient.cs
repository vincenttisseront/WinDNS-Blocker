using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Paths;

namespace DnsBlocker.Core.Ipc;

public sealed class IpcClient : IAsyncDisposable
{
    private readonly string _pipeName;

    public IpcClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? AppPaths.PipeName;
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

    private async Task<T> InvokeAsync<T>(string method, object? parameters, CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellationToken).ConfigureAwait(false);

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
                   ?? throw new InvalidOperationException("Empty IPC response.");

        var response = JsonSerializer.Deserialize<IpcResponse>(line, IpcProtocol.JsonOptions)
                       ?? throw new InvalidOperationException("Invalid IPC response.");

        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? "IPC call failed.");

        if (response.Result is null)
            throw new InvalidOperationException("IPC response missing result.");

        return response.Result.Value.Deserialize<T>(IpcProtocol.JsonOptions)
               ?? throw new InvalidOperationException("Failed to deserialize IPC result.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
