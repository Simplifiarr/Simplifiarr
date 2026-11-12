using System.Net.Http;
using System.Text.Json;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class RadarrClient : IRadarrClient
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public RadarrClient(
        HttpClient httpClient,
        ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    public async Task<List<RadarrMovieRecord>> GetMoviesAsync()
    {
        try
        {
            var json =
                await SendAsync("/api/v3/movie");

            if (json is null)
            {
                return [];
            }

            using var document =
                JsonDocument.Parse(json);

            var movies =
                new List<RadarrMovieRecord>();

            foreach (var movie in document.RootElement.EnumerateArray())
            {
                movies.Add(new RadarrMovieRecord
                {
                    TmdbId = GetInt(movie, "tmdbId"),
                    Title = GetString(movie, "title"),
                    Year = GetNullableInt(movie, "year"),
                    QualityProfileId = GetInt(movie, "qualityProfileId"),
                    Monitored = GetBool(movie, "monitored"),
                    Genres = GetStringArray(movie, "genres"),
                    FilePath = GetMovieFilePath(movie)
                });
            }

            return movies;
        }
        catch
        {
            return [];
        }
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            var json =
                await SendAsync("/api/v3/system/status");

            return json is not null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ArrIdentity?> ParseAsync(
        string releaseName)
    {
        try
        {
            var json =
                await SendAsync($"/api/v3/parse?title={Uri.EscapeDataString(releaseName)}");

            if (json is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("parsedMovieInfo", out var parsed) ||
                parsed.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var title =
                parsed.TryGetProperty("movieTitles", out var titles) && titles.ValueKind == JsonValueKind.Array
                    ? titles.EnumerateArray().Select(item => item.GetString()).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
                    : null;

            var identity =
                new ArrIdentity
                {
                    Source = "Radarr",
                    Title = title ?? GetString(parsed, "movieTitle"),
                    Year = GetNullableInt(parsed, "year") is > 0 and var year ? year : null
                };

            // Radarr has this film: its own title, year and folder win.
            if (root.TryGetProperty("movie", out var movie) &&
                movie.ValueKind == JsonValueKind.Object)
            {
                var path = GetString(movie, "path");

                identity = identity with
                {
                    Title = GetString(movie, "title") is { Length: > 0 } movieTitle ? movieTitle : identity.Title,
                    Year = GetNullableInt(movie, "year") is > 0 and var movieYear ? movieYear : identity.Year,
                    KnownFolder = string.IsNullOrWhiteSpace(path) ? null : path,
                    TmdbId = GetNullableInt(movie, "tmdbId"),
                    ImdbId = GetString(movie, "imdbId") is { Length: > 0 } imdb ? imdb : null
                };
            }

            return string.IsNullOrWhiteSpace(identity.Title) ? null : identity;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<ArrFolderFormats?> GetFolderFormatsAsync()
    {
        try
        {
            var json = await SendAsync("/api/v3/config/naming");

            if (json is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(json);
            var defaults = new ArrFolderFormats();

            return defaults with
            {
                MovieFolderFormat = GetString(document.RootElement, "movieFolderFormat") is { Length: > 0 } format ? format : defaults.MovieFolderFormat
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<string?> SendAsync(
        string relativePath)
    {
        var settings =
            await _settingsService.GetSettingsAsync();

        if (string.IsNullOrWhiteSpace(settings.RadarrUrl) ||
            string.IsNullOrWhiteSpace(settings.RadarrApiKey))
        {
            return null;
        }

        if (!Uri.TryCreate(
                settings.RadarrUrl.TrimEnd('/') + relativePath,
                UriKind.Absolute,
                out var uri))
        {
            return null;
        }

        using var request =
            new HttpRequestMessage(HttpMethod.Get, uri);

        request.Headers.Add("X-Api-Key", settings.RadarrApiKey);

        using var response =
            await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync();
    }

    private static string? GetMovieFilePath(
        JsonElement movie)
    {
        if (!movie.TryGetProperty("movieFile", out var movieFile) ||
            movieFile.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return movieFile.TryGetProperty("path", out var path)
            ? path.GetString()
            : null;
    }

    private static string GetString(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int GetInt(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : 0;
    }

    private static int? GetNullableInt(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;
    }

    private static bool GetBool(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
               property.GetBoolean();
    }

    private static List<string> GetStringArray(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }
}
