using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DnsBlocker.Core.Ipc;
using DnsBlocker.Core.Paths;
using Microsoft.Extensions.Logging;

namespace DnsBlocker.Service;

public sealed class IpcServer
{
    private readonly HubRuntime _runtime;
    private readonly Func<DateTimeOffset?> _nextSchedule;
    private readonly ILogger _logger;
    private readonly string _pipeName;

    public IpcServer(HubRuntime runtime, Func<DateTimeOffset?> nextSchedule, ILogger logger, string? pipeName = null)
    {
        _runtime = runtime;
        _nextSchedule = nextSchedule;
        _logger = logger;
        _pipeName = pipeName ?? AppPaths.PipeName;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var connected = server;
                server = null;
                _ = Task.Run(() => HandleClientAsync(connected, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                _logger.LogError(ex, "IPC accept loop error");
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await using (pipe.ConfigureAwait(false))
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                    return;

                var request = JsonSerializer.Deserialize<IpcRequest>(line, IpcProtocol.JsonOptions);
                if (request is null)
                {
                    await WriteAsync(writer, IpcResponse.Failure("", "Invalid request"), cancellationToken).ConfigureAwait(false);
                    return;
                }

                var response = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
                await WriteAsync(writer, response, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IPC client handling failed");
        }
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Method)
            {
                case IpcProtocol.MethodGetStatus:
                    return IpcResponse.Success(request.Id, _runtime.GetStatus(_nextSchedule()));

                case IpcProtocol.MethodGetConfig:
                    return IpcResponse.Success(request.Id, _runtime.GetConfig());

                case IpcProtocol.MethodSetConfig:
                {
                    var p = request.Params?.Deserialize<SetConfigParams>(IpcProtocol.JsonOptions);
                    if (p?.Config is null)
                        return IpcResponse.Failure(request.Id, "Missing config");
                    return IpcResponse.Success(request.Id, _runtime.SetConfig(p.Config));
                }

                case IpcProtocol.MethodApplyNow:
                {
                    var result = await _runtime.ApplyNowAsync(cancellationToken).ConfigureAwait(false);
                    return IpcResponse.Success(request.Id, result);
                }

                case IpcProtocol.MethodSearchDomains:
                {
                    var p = request.Params?.Deserialize<SearchDomainsParams>(IpcProtocol.JsonOptions) ?? new SearchDomainsParams();
                    return IpcResponse.Success(request.Id, _runtime.SearchDomains(p.Query, p.Limit));
                }

                case IpcProtocol.MethodGetStats:
                {
                    var p = request.Params?.Deserialize<GetStatsParams>(IpcProtocol.JsonOptions) ?? new GetStatsParams();
                    return IpcResponse.Success(request.Id, _runtime.GetStats(p.Hours));
                }

                default:
                    return IpcResponse.Failure(request.Id, $"Unknown method '{request.Method}'");
            }
        }
        catch (Exception ex)
        {
            return IpcResponse.Failure(request.Id, ex.Message);
        }
    }

    private static async Task WriteAsync(StreamWriter writer, IpcResponse response, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, IpcProtocol.JsonOptions);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
