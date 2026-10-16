using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnsBlocker.Core.Updates;

public sealed class UpdateChecker : IUpdateChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;

    public UpdateChecker(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DnsBlocker/1.0");
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string feedUrl,
        string? currentVersion = null,
        CancellationToken cancellationToken = default)
    {
        var current = string.IsNullOrWhiteSpace(currentVersion)
            ? AppVersion.Current
            : AppVersion.StripMetadata(currentVersion);

        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            return new UpdateCheckResult
            {
                UpdateAvailable = false,
                CurrentVersion = current,
                Message = "Update feed URL is not configured."
            };
        }

        using var response = await _http.GetAsync(feedUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var manifest = ParseManifest(json)
                       ?? throw new InvalidOperationException("version.json did not contain a valid manifest.");

        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidOperationException("version.json is missing 'version'.");

        var available = AppVersion.IsNewerThan(manifest.Version, current);
        return new UpdateCheckResult
        {
            UpdateAvailable = available,
            CurrentVersion = current,
            Manifest = manifest,
            Message = available
                ? $"Update available: {manifest.Version}"
                : $"Already up to date ({current})."
        };
    }

    public static UpdateManifest? ParseManifest(string json) =>
        JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
}
