using System.Text.Json;

namespace Simplifiarr.Core.Services;

/// <summary>Who asked for a movie or show in Seerr (Jellyseerr or Overseerr).</summary>
public sealed record SeerrRequest(
    int MediaId,
    string JellyfinItemId,
    string RequestedBy,
    string RequesterJellyfinUserId,
    DateTime? RequestedUtc);

public interface ISeerrClient
{
    Task<bool> IsSetUpAsync();

    /// <summary>Null when it works, otherwise what went wrong.</summary>
    Task<string?> TestConnectionAsync(string url, string apiKey);

    /// <summary>Every request that has reached Jellyfin. Empty when not set up.</summary>
    Task<IReadOnlyList<SeerrRequest>> GetRequestsAsync();

    /// <summary>
    /// Forgets a movie or show, so it can be requested again after it is
    /// deleted. Failure is not an error.
    /// </summary>
    Task ForgetMediaAsync(int mediaId);
}

public class SeerrClient : ISeerrClient
{
    private const int PageSize = 100;

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;

    public SeerrClient(HttpClient http, ISettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    public async Task<bool> IsSetUpAsync()
    {
        var settings = await _settings.GetSettingsAsync();

        return !string.IsNullOrWhiteSpace(settings.SeerrUrl) &&
               !string.IsNullOrWhiteSpace(settings.SeerrApiKey);
    }

    public async Task<string?> TestConnectionAsync(string url, string apiKey)
    {
        if (!Uri.TryCreate(url.TrimEnd('/') + "/api/v1/settings/main", UriKind.Absolute, out var uri))
        {
            return "That isn't a valid address.";
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("X-Api-Key", apiKey);

            using var response = await _http.SendAsync(request);

            return response.IsSuccessStatusCode
                ? null
                : (int)response.StatusCode is 401 or 403
                    ? "Seerr didn't accept the API key."
                    : $"Seerr answered {(int)response.StatusCode}.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return $"Couldn't reach Seerr at {url}.";
        }
    }

    public async Task<IReadOnlyList<SeerrRequest>> GetRequestsAsync()
    {
        var settings = await _settings.GetSettingsAsync();

        if (!await IsSetUpAsync())
        {
            return [];
        }

        var requests = new List<SeerrRequest>();

        for (var skip = 0; ; skip += PageSize)
        {
            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{settings.SeerrUrl.TrimEnd('/')}/api/v1/request?take={PageSize}&skip={skip}&filter=all&sort=added");

            request.Headers.Add("X-Api-Key", settings.SeerrApiKey);

            using var response = await _http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Seerr answered {(int)response.StatusCode} when asked for its requests.");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            var page = document.RootElement.GetProperty("results");

            foreach (var entry in page.EnumerateArray())
            {
                if (!entry.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var jellyfinId = Text(media, "jellyfinMediaId");

                if (jellyfinId.Length == 0)
                {
                    jellyfinId = Text(media, "jellyfinMediaId4k");
                }

                if (jellyfinId.Length == 0)
                {
                    continue;
                }

                var user = entry.TryGetProperty("requestedBy", out var by) ? by : default;

                var name =
                    user.ValueKind != JsonValueKind.Object
                        ? "Someone"
                        : FirstText(user, "displayName", "jellyfinUsername", "username", "email");

                requests.Add(
                    new SeerrRequest(
                        media.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : 0,
                        jellyfinId,
                        name,
                        user.ValueKind == JsonValueKind.Object ? Text(user, "jellyfinUserId") : string.Empty,
                        entry.TryGetProperty("createdAt", out var created) && created.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(created.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var when)
                            ? when
                            : null));
            }

            if (page.GetArrayLength() < PageSize)
            {
                break;
            }
        }

        return requests;
    }

    public async Task ForgetMediaAsync(int mediaId)
    {
        var settings = await _settings.GetSettingsAsync();

        if (mediaId <= 0 || !await IsSetUpAsync())
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{settings.SeerrUrl.TrimEnd('/')}/api/v1/media/{mediaId}");
            request.Headers.Add("X-Api-Key", settings.SeerrApiKey);

            using var _ = await _http.SendAsync(request);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Utilities.AppLog.Warn("Couldn't tell Seerr a title was deleted.", exception);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string FirstText(JsonElement element, params string[] names) =>
        names.Select(name => Text(element, name)).FirstOrDefault(value => value.Length > 0) ?? "Someone";
}
