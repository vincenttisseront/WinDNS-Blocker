using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DnsBlocker.Core.Paths;

namespace DnsBlocker.Core.Security;

/// <summary>
/// Stores DNS-target admin credentials under ProgramData using DPAPI (LocalMachine)
/// so the elevated hub service can decrypt them. Passwords are never written to logs.
/// On non-Windows, uses a dev-only XOR placeholder (not for production).
/// </summary>
public sealed class SecureCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public void Save(string hostname, string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        AppPaths.EnsureDirectories();

        var key = NormalizeKey(hostname);
        var payload = new CredentialPayload
        {
            Hostname = hostname.Trim(),
            Username = username.Trim(),
            PasswordUtf8 = Encoding.UTF8.GetBytes(password)
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var protectedBytes = Protect(json);
        File.WriteAllBytes(BlobPath(key), protectedBytes);

        var meta = new CredentialMeta
        {
            Hostname = payload.Hostname,
            Username = payload.Username,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        File.WriteAllText(MetaPath(key), JsonSerializer.Serialize(meta, JsonOptions));
        CryptographicOperations.ZeroMemory(payload.PasswordUtf8);
    }

    public bool TryLoad(string hostname, out string username, out string password)
    {
        username = "";
        password = "";
        var key = NormalizeKey(hostname);
        var path = BlobPath(key);
        if (!File.Exists(path))
            return false;

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var json = Unprotect(protectedBytes);
            var payload = JsonSerializer.Deserialize<CredentialPayload>(json, JsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Username) || payload.PasswordUtf8 is null)
                return false;

            username = payload.Username;
            password = Encoding.UTF8.GetString(payload.PasswordUtf8);
            CryptographicOperations.ZeroMemory(payload.PasswordUtf8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Delete(string hostname)
    {
        var key = NormalizeKey(hostname);
        TryDelete(BlobPath(key));
        TryDelete(MetaPath(key));
    }

    public CredentialMeta? GetMeta(string hostname)
    {
        var path = MetaPath(NormalizeKey(hostname));
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<CredentialMeta>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Protect(byte[] data)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Protect(data, optionalEntropy: GetEntropy(), scope: DataProtectionScope.LocalMachine);

        // Dev/Linux: not secure — tests only
        var copy = (byte[])data.Clone();
        var ent = GetEntropy();
        for (var i = 0; i < copy.Length; i++)
            copy[i] ^= ent[i % ent.Length];
        return copy;
    }

    private static byte[] Unprotect(byte[] data)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Unprotect(data, optionalEntropy: GetEntropy(), scope: DataProtectionScope.LocalMachine);

        var copy = (byte[])data.Clone();
        var ent = GetEntropy();
        for (var i = 0; i < copy.Length; i++)
            copy[i] ^= ent[i % ent.Length];
        return copy;
    }

    private static byte[] GetEntropy() =>
        Encoding.UTF8.GetBytes("DnsBlocker.TargetCreds.v1");

    private static string NormalizeKey(string hostname)
    {
        var h = hostname.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(h));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private static string BlobPath(string key) => Path.Combine(AppPaths.SecretsDirectory, key + ".dpapi");
    private static string MetaPath(string key) => Path.Combine(AppPaths.SecretsDirectory, key + ".meta.json");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private sealed class CredentialPayload
    {
        public string Hostname { get; set; } = "";
        public string Username { get; set; } = "";
        public byte[] PasswordUtf8 { get; set; } = [];
    }

    public sealed class CredentialMeta
    {
        public string Hostname { get; set; } = "";
        public string Username { get; set; } = "";
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}
