using System.Text.Json;
using System.Text.Json.Serialization;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Paths;

namespace DnsBlocker.Core.Config;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly object _gate = new();

    public ConfigStore(string? path = null)
    {
        _path = path ?? AppPaths.ConfigPath;
    }

    public AppConfig Load()
    {
        lock (_gate)
        {
            AppPaths.EnsureDirectories();
            if (!File.Exists(_path))
            {
                var defaults = new AppConfig();
                Save(defaults);
                return defaults;
            }

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        lock (_gate)
        {
            AppPaths.EnsureDirectories();
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(_path, json);
        }
    }
}
