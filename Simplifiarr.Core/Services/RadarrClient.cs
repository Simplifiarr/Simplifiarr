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
