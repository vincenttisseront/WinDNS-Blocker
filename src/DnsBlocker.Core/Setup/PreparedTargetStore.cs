using System.Text.Json;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Paths;

namespace DnsBlocker.Core.Setup;

public sealed class PreparedTargetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly object _gate = new();

    public PreparedTargetStore(string? path = null)
    {
        _path = path ?? AppPaths.PreparedTargetsPath;
    }

    public IReadOnlyList<PreparedTargetInfo> List()
    {
        lock (_gate)
        {
            return LoadUnlocked().OrderBy(t => t.Hostname, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public PreparedTargetInfo? Get(string hostname)
    {
        var key = hostname.Trim();
        lock (_gate)
        {
            return LoadUnlocked().FirstOrDefault(t =>
                t.Hostname.Equals(key, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Upsert(PreparedTargetInfo info)
    {
        lock (_gate)
        {
            AppPaths.EnsureDirectories();
            var list = LoadUnlocked();
            list.RemoveAll(t => t.Hostname.Equals(info.Hostname, StringComparison.OrdinalIgnoreCase));
            list.Add(info);
            SaveUnlocked(list);
        }
    }

    public void Remove(string hostname)
    {
        lock (_gate)
        {
            var list = LoadUnlocked();
            list.RemoveAll(t => t.Hostname.Equals(hostname.Trim(), StringComparison.OrdinalIgnoreCase));
            SaveUnlocked(list);
        }
    }

    private List<PreparedTargetInfo> LoadUnlocked()
    {
        if (!File.Exists(_path))
            return [];
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<PreparedTargetInfo>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveUnlocked(List<PreparedTargetInfo> list)
    {
        AppPaths.EnsureDirectories();
        File.WriteAllText(_path, JsonSerializer.Serialize(list, JsonOptions));
    }
}
