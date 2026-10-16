using DnsBlocker.Core.Models;
using DnsBlocker.Core.Security;
using DnsBlocker.Core.Setup;

namespace DnsBlocker.Core.Tests;

public class TargetPrepTests
{
    [Fact]
    public void SecureCredentialStore_RoundTrips_WithoutPlainFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dnsblocker-creds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Point secrets via env-less approach: instantiate store that uses AppPaths — override by writing relative.
            // Use real AppPaths under temp by temporarily setting HOME-like path isn't easy; test Protect via store methods
            // against default path under temp user profile — use unique hostname and cleanup.
            var store = new SecureCredentialStore();
            var host = "windc-test-" + Guid.NewGuid().ToString("N")[..8] + ".lab.local";
            store.Save(host, @"LAB\Administrator", "S3cret!Pass");
            Assert.True(store.TryLoad(host, out var user, out var pass));
            Assert.Equal(@"LAB\Administrator", user);
            Assert.Equal("S3cret!Pass", pass);
            var meta = store.GetMeta(host);
            Assert.NotNull(meta);
            Assert.Equal(@"LAB\Administrator", meta!.Username);
            // Meta file must not contain password
            var secretsDir = DnsBlocker.Core.Paths.AppPaths.SecretsDirectory;
            foreach (var file in Directory.GetFiles(secretsDir, "*.meta.json"))
            {
                if (File.ReadAllText(file).Contains("S3cret", StringComparison.Ordinal))
                    Assert.Fail("Password leaked into meta.json");
            }

            store.Delete(host);
            Assert.False(store.TryLoad(host, out _, out _));
        }
        finally
        {
            // best-effort cleanup of this host only already deleted
        }
    }

    [Fact]
    public async Task MockTargetPrepRunner_Prepare_Succeeds()
    {
        var runner = new MockTargetPrepRunner();
        var result = await runner.PrepareAsync(new PrepareDnsTargetRequest
        {
            Hostname = "windc05.ar-systems.fr",
            Username = @"AR\admin",
            Password = "x",
            AddHubToDnsAdmins = true,
            HubIdentityToGrant = "DnsAdmins"
        });

        Assert.True(result.Ok);
        Assert.NotNull(result.Target);
        Assert.Equal("windc05.ar-systems.fr", result.Target!.Hostname);
        Assert.Contains(result.Log, l => l.Contains("ACL", StringComparison.OrdinalIgnoreCase));
        Assert.True(runner.SawPassword);
    }

    [Fact]
    public void PreparedTargetStore_UpsertList()
    {
        var path = Path.Combine(Path.GetTempPath(), "prepared-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new PreparedTargetStore(path);
            store.Upsert(new PreparedTargetInfo
            {
                Hostname = "dns1.lab.local",
                Prepared = true,
                PreparedAtUtc = DateTimeOffset.UtcNow,
                HubIdentityGranted = "DnsAdmins"
            });
            Assert.Single(store.List());
            Assert.Equal("dns1.lab.local", store.Get("dns1.lab.local")!.Hostname);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
