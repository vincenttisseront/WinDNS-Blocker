using DnsBlocker.Core.Config;
using DnsBlocker.Core.Dns;
using DnsBlocker.Core.Ingest;
using DnsBlocker.Core.Logging;
using DnsBlocker.Core.Paths;
using DnsBlocker.Core.Stats;
using DnsBlocker.Core.Storage;
using DnsBlocker.Core.Sync;
using DnsBlocker.Core.Updates;
using DnsBlocker.Service;

// Windows services often start with cwd = System32. Pin to the install directory.
try
{
    var baseDir = AppContext.BaseDirectory;
    if (!string.IsNullOrWhiteSpace(baseDir) && Directory.Exists(baseDir))
        Directory.SetCurrentDirectory(baseDir);
}
catch
{
    // best-effort
}

try
{
    AppPaths.EnsureDirectories();
    AppPaths.TryHardenDataDirectoryAcl();

    var builder = Host.CreateApplicationBuilder(args);
    builder.Environment.ContentRootPath = AppContext.BaseDirectory;
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
    builder.Services.AddSingleton<IUpdateChecker, UpdateChecker>();
    builder.Services.AddSingleton<UpdateInstaller>();
    builder.Services.AddSingleton<HubRuntime>();
    builder.Services.AddHostedService<HubHostedService>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    WriteStartupFailure("DnsBlocker.Service failed to start", ex);
    throw;
}

static void WriteStartupFailure(string title, Exception ex)
{
    try
    {
        AppPaths.EnsureDirectories();
        var path = Path.Combine(AppPaths.LogDirectory, "service-startup-failure.log");
        File.AppendAllText(path,
            $"{DateTimeOffset.Now:u} {title}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        // Also write service-crash.log for compatibility with Domaines UX messaging.
        var crashLog = Path.Combine(AppPaths.LogDirectory, "service-crash.log");
        File.AppendAllText(crashLog, $"{DateTimeOffset.Now:o} FATAL: {ex}\n");
    }
    catch
    {
        // last resort: ignore
    }
}
