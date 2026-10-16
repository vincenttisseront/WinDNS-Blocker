using System.Security.Cryptography;
using System.Text;
using DnsBlocker.Core.Updates;
using RichardSzalay.MockHttp;

namespace DnsBlocker.Core.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.2.3", "1.2.3+abc", 0)]
    [InlineData("1.0.0", "1.0.0-beta", 1)]
    [InlineData("1.0.0-beta", "1.0.0-alpha", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    public void CompareSemVer_OrdersVersions(string left, string right, int expectedSign)
    {
        var cmp = AppVersion.CompareSemVer(left, right);
        Assert.Equal(expectedSign, Math.Sign(cmp));
    }

    [Fact]
    public void ParseManifest_ReadsVersionJsonFields()
    {
        const string json = """
            {
              "version": "1.2.3",
              "msiUrl": "https://example.test/DnsBlocker-1.2.3.msi",
              "sha256": "ABCDEF0123456789",
              "releaseNotes": "Correctifs DNS",
              "publishedAt": "2026-10-16T10:00:00Z"
            }
            """;

        var manifest = UpdateChecker.ParseManifest(json);
        Assert.NotNull(manifest);
        Assert.Equal("1.2.3", manifest!.Version);
        Assert.Equal("https://example.test/DnsBlocker-1.2.3.msi", manifest.MsiUrl);
        Assert.Equal("ABCDEF0123456789", manifest.Sha256);
        Assert.Equal("Correctifs DNS", manifest.ReleaseNotes);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero), manifest.PublishedAt);
    }

    [Fact]
    public async Task CheckAsync_ReportsUpdateWhenFeedIsNewer()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://feed.test/version.json")
            .Respond("application/json", """
                {
                  "version": "9.9.9",
                  "msiUrl": "https://feed.test/app.msi",
                  "sha256": "deadbeef",
                  "releaseNotes": "test",
                  "publishedAt": "2026-01-01T00:00:00Z"
                }
                """);

        var checker = new UpdateChecker(mockHttp.ToHttpClient());
        var result = await checker.CheckAsync("https://feed.test/version.json", currentVersion: "1.0.0");

        Assert.True(result.UpdateAvailable);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("9.9.9", result.Manifest!.Version);
        Assert.Equal("https://feed.test/app.msi", result.Manifest.MsiUrl);
    }

    [Fact]
    public async Task CheckAsync_NoUpdateWhenCurrentIsSameOrNewer()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://feed.test/version.json")
            .Respond("application/json", """
                {
                  "version": "1.0.0",
                  "msiUrl": "https://feed.test/app.msi",
                  "sha256": "deadbeef"
                }
                """);

        var checker = new UpdateChecker(mockHttp.ToHttpClient());
        var result = await checker.CheckAsync("https://feed.test/version.json", currentVersion: "1.0.0");

        Assert.False(result.UpdateAvailable);
        Assert.NotNull(result.Manifest);
    }

    [Fact]
    public async Task CheckAsync_EmptyFeedUrl_ReturnsMessage()
    {
        var checker = new UpdateChecker(new MockHttpMessageHandler().ToHttpClient());
        var result = await checker.CheckAsync("", currentVersion: "1.0.0");
        Assert.False(result.UpdateAvailable);
        Assert.False(result.FeedError);
        Assert.Contains("not configured", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_Feed404_ReturnsFriendlyFeedError()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://github.com/vincenttisseront/WinDNS-Blocker/releases/latest/download/version.json")
            .Respond(System.Net.HttpStatusCode.NotFound);

        var checker = new UpdateChecker(mockHttp.ToHttpClient());
        var result = await checker.CheckAsync(UpdateFeedUrls.DefaultPublicFeedUrl, currentVersion: "1.0.0");

        Assert.False(result.UpdateAvailable);
        Assert.True(result.FeedError);
        Assert.Contains("404", result.Message);
        Assert.Contains("introuvable", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("does not indicate success", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_RewritesBadLatestDownloadPath()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When(UpdateFeedUrls.DefaultPublicFeedUrl)
            .Respond("application/json", """
                {
                  "version": "1.0.11",
                  "msiUrl": "https://feed.test/app.msi",
                  "sha256": "deadbeef"
                }
                """);

        var checker = new UpdateChecker(mockHttp.ToHttpClient());
        // Common typo that 404s on GitHub: /releases/download/latest/…
        var result = await checker.CheckAsync(
            "https://github.com/vincenttisseront/WinDNS-Blocker/releases/download/latest/version.json",
            currentVersion: "1.0.0");

        Assert.True(result.UpdateAvailable);
        Assert.False(result.FeedError);
        Assert.Equal("1.0.11", result.Manifest!.Version);
    }

    [Fact]
    public void Resolve_RewritesDownloadLatestPath()
    {
        var fixedUrl = UpdateFeedUrls.Resolve(
            "https://github.com/vincenttisseront/WinDNS-Blocker/releases/download/latest/version.json");
        Assert.Equal(UpdateFeedUrls.DefaultPublicFeedUrl, fixedUrl);
    }

    [Fact]
    public void AppConfig_DefaultUpdateFeedUrl_IsStableLatest()
    {
        var cfg = new DnsBlocker.Core.Models.AppConfig();
        Assert.Equal(UpdateFeedUrls.DefaultPublicFeedUrl, cfg.UpdateFeedUrl);
    }

    [Fact]
    public async Task InstallAsync_VerifiesSha256AndLaunchesMsiexec()
    {
        var msiBytes = Encoding.UTF8.GetBytes("fake-msi-payload");
        var expectedHash = Convert.ToHexString(SHA256.HashData(msiBytes));

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://feed.test/app.msi")
            .Respond("application/octet-stream", new MemoryStream(msiBytes));

        var files = new MemoryUpdateFileSystem();
        var launcher = new RecordingProcessLauncher();
        var installer = new UpdateInstaller(mockHttp.ToHttpClient(), files, launcher, waitForExit: true);

        var result = await installer.InstallAsync(new UpdateManifest
        {
            Version = "2.0.0",
            MsiUrl = "https://feed.test/app.msi",
            Sha256 = expectedHash
        }, silent: true);

        Assert.True(result.Started);
        Assert.True(result.Success);
        Assert.NotNull(result.MsiPath);
        Assert.Contains("/qn", launcher.LastArguments);
        Assert.Contains(result.MsiPath!, launcher.LastArguments);
        Assert.Contains("msiexec.exe", launcher.LastArguments);
        Assert.Contains("taskkill", launcher.LastArguments, StringComparison.OrdinalIgnoreCase);
        // Silent path: kill UI, delay, detach via cmd start /b so StopServices cannot kill msiexec.
        Assert.EndsWith("cmd.exe", launcher.LastFileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_RejectsBadHash()
    {
        var msiBytes = Encoding.UTF8.GetBytes("fake-msi-payload");
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://feed.test/app.msi")
            .Respond("application/octet-stream", new MemoryStream(msiBytes));

        var installer = new UpdateInstaller(
            mockHttp.ToHttpClient(),
            new MemoryUpdateFileSystem(),
            new RecordingProcessLauncher());

        var result = await installer.InstallAsync(new UpdateManifest
        {
            Version = "2.0.0",
            MsiUrl = "https://feed.test/app.msi",
            Sha256 = new string('0', 64)
        });

        Assert.False(result.Started);
        Assert.Contains("SHA-256", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_RejectsInvalidShaFormatWithoutDownload()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://feed.test/app.msi")
            .Respond("application/octet-stream", new MemoryStream("x"u8.ToArray()));

        var installer = new UpdateInstaller(
            mockHttp.ToHttpClient(),
            new MemoryUpdateFileSystem(),
            new RecordingProcessLauncher());

        var result = await installer.InstallAsync(new UpdateManifest
        {
            Version = "2.0.0",
            MsiUrl = "https://feed.test/app.msi",
            Sha256 = "deadbeef"
        });

        Assert.False(result.Started);
        Assert.Contains("SHA-256", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("64", result.Error);
    }

    [Fact]
    public async Task InstallAsync_HttpFailure_ReturnsErrorNotThrow()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://feed.test/app.msi")
            .Respond(System.Net.HttpStatusCode.NotFound);

        var installer = new UpdateInstaller(
            mockHttp.ToHttpClient(),
            new MemoryUpdateFileSystem(),
            new RecordingProcessLauncher());

        var result = await installer.InstallAsync(new UpdateManifest
        {
            Version = "2.0.0",
            MsiUrl = "https://feed.test/app.msi",
            Sha256 = new string('a', 64)
        });

        Assert.False(result.Started);
        Assert.Contains("HTTP", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_MsiexecStartFailure_ReturnsErrorNotThrow()
    {
        var msiBytes = Encoding.UTF8.GetBytes("fake-msi-payload");
        var expectedHash = Convert.ToHexString(SHA256.HashData(msiBytes));

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://feed.test/app.msi")
            .Respond("application/octet-stream", new MemoryStream(msiBytes));

        var installer = new UpdateInstaller(
            mockHttp.ToHttpClient(),
            new MemoryUpdateFileSystem(),
            new ThrowingProcessLauncher());

        var result = await installer.InstallAsync(new UpdateManifest
        {
            Version = "2.0.0",
            MsiUrl = "https://feed.test/app.msi",
            Sha256 = expectedHash
        }, silent: true);

        Assert.False(result.Started);
        Assert.Contains("msiexec", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_MalformedJson_ReturnsFeedErrorNotThrow()
    {
        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://feed.test/version.json")
            .Respond("application/json", "{ not-json");

        var checker = new UpdateChecker(mockHttp.ToHttpClient());
        var result = await checker.CheckAsync("https://feed.test/version.json", currentVersion: "1.0.0");

        Assert.False(result.UpdateAvailable);
        Assert.True(result.FeedError);
        Assert.Contains("JSON", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_UsesNorestartAndDoesNotWaitByDefault()
    {
        var msiBytes = Encoding.UTF8.GetBytes("fake-msi-payload");
        var expectedHash = Convert.ToHexString(SHA256.HashData(msiBytes));

        var mockHttp = new MockHttpMessageHandler();
        mockHttp.When("https://feed.test/app.msi")
            .Respond("application/octet-stream", new MemoryStream(msiBytes));

        var files = new MemoryUpdateFileSystem();
        var launcher = new RecordingProcessLauncher();
        var installer = new UpdateInstaller(mockHttp.ToHttpClient(), files, launcher, waitForExit: false);

        var result = await installer.InstallAsync(new UpdateManifest
        {
            Version = "2.0.0",
            MsiUrl = "https://feed.test/app.msi",
            Sha256 = expectedHash
        }, silent: true);

        Assert.True(result.Started);
        Assert.Contains("/norestart", launcher.LastArguments);
        Assert.False(launcher.LastWaitForExit);
        Assert.Null(result.ExitCode);
    }

    private sealed class MemoryUpdateFileSystem : IUpdateFileSystem
    {
        private readonly Dictionary<string, MemoryStream> _files = new(StringComparer.OrdinalIgnoreCase);

        public string GetTempFilePath(string extension) =>
            Path.Combine(Path.GetTempPath(), "test-" + Guid.NewGuid().ToString("N") + extension);

        public Stream OpenWrite(string path)
        {
            var ms = new MemoryStream();
            _files[path] = ms;
            return new NonDisposingStream(ms);
        }

        public Stream OpenRead(string path)
        {
            if (!_files.TryGetValue(path, out var ms))
                throw new FileNotFoundException(path);
            return new MemoryStream(ms.ToArray());
        }

        public void TryDelete(string path) => _files.Remove(path);
    }

    /// <summary>Keeps the backing MemoryStream alive after dispose of the write wrapper.</summary>
    private sealed class NonDisposingStream : Stream
    {
        private readonly MemoryStream _inner;
        public NonDisposingStream(MemoryStream inner) => _inner = inner;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { /* keep inner */ }
    }

    private sealed class RecordingProcessLauncher : IProcessLauncher
    {
        public string? LastFileName { get; private set; }
        public string? LastArguments { get; private set; }
        public bool LastWaitForExit { get; private set; }

        public Task<int?> StartAsync(System.Diagnostics.ProcessStartInfo startInfo, bool waitForExit, CancellationToken cancellationToken)
        {
            LastFileName = startInfo.FileName;
            LastArguments = startInfo.Arguments;
            LastWaitForExit = waitForExit;
            return Task.FromResult<int?>(waitForExit ? 0 : null);
        }
    }

    private sealed class ThrowingProcessLauncher : IProcessLauncher
    {
        public Task<int?> StartAsync(System.Diagnostics.ProcessStartInfo startInfo, bool waitForExit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Failed to start msiexec.");
    }
}
