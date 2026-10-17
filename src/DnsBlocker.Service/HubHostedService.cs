using DnsBlocker.Core.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DnsBlocker.Service;

public sealed class HubHostedService : BackgroundService
{
    private readonly HubRuntime _runtime;
    private readonly FileLogBuffer _logs;
    private readonly ILogger<HubHostedService> _logger;
    private DateTimeOffset? _nextScheduledUtc;
    private DateTimeOffset _nextUpdateCheckUtc;
    private DateTimeOffset _nextStatsPollUtc;

    public HubHostedService(HubRuntime runtime, FileLogBuffer logs, ILogger<HubHostedService> logger)
    {
        _runtime = runtime;
        _logs = logs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logs.Write("DnsBlocker hub service starting");
        _runtime.ApplyStatsSetting();
        _nextScheduledUtc = _runtime.ComputeNextScheduledUtc(DateTimeOffset.Now);
        // Stagger first update check shortly after start, then daily.
        _nextUpdateCheckUtc = DateTimeOffset.UtcNow.AddMinutes(2);
        _nextStatsPollUtc = DateTimeOffset.UtcNow.AddSeconds(20);

        var ipc = new IpcServer(_runtime, () => _nextScheduledUtc, _logger);
        var ipcTask = ipc.RunAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = DateTimeOffset.UtcNow;
                if (_nextScheduledUtc is not null && nowUtc >= _nextScheduledUtc.Value)
                {
                    _logs.Write("Scheduled sync triggered");
                    try
                    {
                        await _runtime.ApplyNowAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Scheduled sync failed");
                        _logs.Write($"Scheduled sync failed: {ex.Message}");
                    }

                    _nextScheduledUtc = _runtime.ComputeNextScheduledUtc(DateTimeOffset.Now);
                }

                if (nowUtc >= _nextUpdateCheckUtc)
                {
                    try
                    {
                        await _runtime.RunScheduledUpdateCheckAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Scheduled update check failed");
                        _logs.Write($"Scheduled update check failed: {ex.Message}");
                    }

                    _nextUpdateCheckUtc = DateTimeOffset.UtcNow.AddDays(1);
                }

                if (nowUtc >= _nextStatsPollUtc)
                {
                    try
                    {
                        await _runtime.PollStatsAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Stats poll failed");
                        _logs.Write($"Stats poll failed: {ex.Message}");
                    }

                    _nextStatsPollUtc = DateTimeOffset.UtcNow.AddSeconds(30);
                }

                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        finally
        {
            _logs.Write("DnsBlocker hub service stopping");
            try { await ipcTask.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }
}
