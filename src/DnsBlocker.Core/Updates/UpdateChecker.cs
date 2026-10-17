using System.Net;
using System.Net.Http;
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

        var resolved = UpdateFeedUrls.Resolve(feedUrl);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return new UpdateCheckResult
            {
                UpdateAvailable = false,
                CurrentVersion = current,
                Message = "URL du flux de mise à jour non configurée. / Update feed URL is not configured."
            };
        }

        try
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(resolved, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return FeedError(current, resolved,
                    "Délai dépassé en contactant le flux de mise à jour. / Update feed request timed out.");
            }
            catch (HttpRequestException ex)
            {
                return FeedError(current, resolved,
                    "Impossible de joindre le flux de mise à jour. / Cannot reach update feed. " +
                    Truncate(ex.Message, 120));
            }
            catch (IOException ex)
            {
                return FeedError(current, resolved,
                    "Erreur I/O en contactant le flux de mise à jour. / I/O error contacting update feed. " +
                    Truncate(ex.Message, 120));
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return FeedError(current, resolved, FormatHttpStatusMessage(response.StatusCode, resolved));
                }

                string json;
                try
                {
                    json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    return FeedError(current, resolved,
                        "Impossible de lire version.json. / Failed to read version.json. " +
                        Truncate(ex.Message, 120));
                }

                UpdateManifest? manifest;
                try
                {
                    manifest = ParseManifest(json);
                }
                catch (JsonException)
                {
                    return FeedError(current, resolved,
                        "version.json invalide (JSON). / version.json did not contain a valid manifest.");
                }

                if (manifest is null)
                {
                    return FeedError(current, resolved,
                        "version.json invalide (JSON). / version.json did not contain a valid manifest.");
                }

                if (string.IsNullOrWhiteSpace(manifest.Version))
                {
                    return FeedError(current, resolved,
                        "version.json sans champ « version ». / version.json is missing 'version'.");
                }

                var available = AppVersion.IsNewerThan(manifest.Version, current);
                return new UpdateCheckResult
                {
                    UpdateAvailable = available,
                    CurrentVersion = current,
                    Manifest = manifest,
                    Message = available
                        ? $"Mise à jour disponible : {manifest.Version} / Update available: {manifest.Version}"
                        : $"À jour ({current}). / Already up to date ({current})."
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let unexpected failures bubble as unhandled — UI/IPC stay alive.
            return FeedError(current, resolved,
                "Échec de la vérification des mises à jour. / Update check failed. " +
                Truncate(ex.Message, 160));
        }
    }

    public static UpdateManifest? ParseManifest(string json) =>
        JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);

    /// <summary>User-facing French (+ EN) text for feed HTTP failures — avoids raw HttpRequestException dumps.</summary>
    public static string FormatHttpStatusMessage(HttpStatusCode statusCode, string feedUrl)
    {
        var code = (int)statusCode;
        if (statusCode == HttpStatusCode.NotFound)
        {
            return
                "Flux de mise à jour introuvable (404). Vérifiez l'URL version.json — " +
                $"recommandé : {UpdateFeedUrls.DefaultPublicFeedUrl}. " +
                $"(URL essayée : {feedUrl}) / " +
                "Update feed not found (404). Check the version.json URL — " +
                $"recommended: {UpdateFeedUrls.DefaultPublicFeedUrl}.";
        }

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return
                $"Accès au flux refusé ({code}). / Update feed access denied ({code}).";
        }

        return
            $"Le serveur du flux a répondu {code} ({statusCode}). / " +
            $"Update feed returned HTTP {code} ({statusCode}).";
    }

    private static UpdateCheckResult FeedError(string current, string feedUrl, string message) =>
        new()
        {
            UpdateAvailable = false,
            FeedError = true,
            CurrentVersion = current,
            Message = message
        };

    private static string Truncate(string message, int max)
    {
        if (string.IsNullOrEmpty(message) || message.Length <= max)
            return message;
        return message[..max] + "…";
    }
}
