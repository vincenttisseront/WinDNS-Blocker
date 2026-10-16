using DnsBlocker.Core.Config;
using DnsBlocker.Core.Dns;
using DnsBlocker.Core.Ingest;
using DnsBlocker.Core.Logging;
using DnsBlocker.Core.Paths;
using DnsBlocker.Core.Stats;
using DnsBlocker.Core.Storage;
using DnsBlocker.Core.Sync;
using DnsBlocker.Service;

AppPaths.EnsureDirectories();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "DnsBlocker");

builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<FileLogBuffer>();
builder.Services.AddSingleton<BlocklistIngestor>();
builder.Services.AddSingleton<IDnsPolicyProvider>(sp =>
{
    if (OperatingSystem.IsWindows()
        && !string.Equals(Environment.GetEnvironmentVariable("DNSBLOCKER_USE_MOCK"), "1", StringComparison.Ordinal))
    {
        return new PowerShellDnsPolicyProvider(sp.GetService<ILogger<PowerShellDnsPolicyProvider>>());
    }

    var logger = sp.GetService<ILogger<MockDnsPolicyProvider>>();
    logger?.LogWarning("Using MockDnsPolicyProvider (non-Windows or DNSBLOCKER_USE_MOCK=1)");
    return new MockDnsPolicyProvider();
});
builder.Services.AddSingleton<SyncEngine>();
builder.Services.AddSingleton<DnsStatsCollector>();
builder.Services.AddSingleton<HubRuntime>();
builder.Services.AddHostedService<HubHostedService>();

var host = builder.Build();
host.Run();
