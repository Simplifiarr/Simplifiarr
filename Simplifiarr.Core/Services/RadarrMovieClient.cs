using System.Text.Json;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>What Rolling Movies needs from Radarr.</summary>
public interface IRadarrMovieClient
{
    /// <summary>Every movie that has a file, with its tags by name. Empty when Radarr isn't set up.</summary>
    Task<IReadOnlyList<RollingMovie>> GetMoviesWithFilesAsync();

    /// <summary>Every tag Radarr has, by name.</summary>
    Task<IReadOnlyList<string>> GetTagsAsync();

    /// <summary>Radarr's root folders.</summary>
    Task<IReadOnlyList<string>> GetRootFoldersAsync();
}

public class RadarrMovieClient : IRadarrMovieClient
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;

    public RadarrMovieClient(HttpClient http, ISettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    public async Task<IReadOnlyList<RollingMovie>> GetMoviesWithFilesAsync()
    {
        using var tags = await GetAsync("/api/v3/tag");
        using var movies = await GetAsync("/api/v3/movie");

        if (tags is null || movies is null)
        {
            return [];
        }

        var tagNames = TagNames(tags);
        var result = new List<RollingMovie>();

        foreach (var movie in movies.RootElement.EnumerateArray())
        {
            if (!movie.TryGetProperty("hasFile", out var hasFile) || hasFile.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            var file = movie.TryGetProperty("movieFile", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;

            result.Add(
                new RollingMovie(
                    movie.GetProperty("id").GetInt32(),
                    Text(movie, "title"),
                    movie.TryGetProperty("year", out var year) && year.ValueKind == JsonValueKind.Number && year.GetInt32() > 0 ? year.GetInt32() : null,
                    Text(movie, "path"),
                    movie.TryGetProperty("sizeOnDisk", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0,
                    Arrived(movie, file),
                    movie.TryGetProperty("tags", out var ids) && ids.ValueKind == JsonValueKind.Array
                        ? ids.EnumerateArray()
                            .Where(id => id.ValueKind == JsonValueKind.Number)
                            .Select(id => tagNames.GetValueOrDefault(id.GetInt32()))
                            .OfType<string>()
                            .ToList()
                        : []));
        }

        return result.OrderBy(movie => movie.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync()
    {
        using var tags = await GetAsync("/api/v3/tag");

        return tags is null ? [] : TagNames(tags).Values.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<string>> GetRootFoldersAsync()
    {
        using var folders = await GetAsync("/api/v3/rootfolder");

        return folders is null
            ? []
            : folders.RootElement.EnumerateArray()
                .Select(folder => Text(folder, "path"))
                .Where(path => path.Length > 0)
                .ToList();
    }

    /// <summary>
    /// When the movie most likely arrived. The file's own date is reset
    /// whenever Radarr re-imports it (after a move, say), so it only caps
    /// the estimate: added to Radarr, or released, whichever is later.
    /// </summary>
    public static DateTime? Arrived(JsonElement movie, JsonElement file)
    {
        var fileAdded = file.ValueKind == JsonValueKind.Object ? Date(file, "dateAdded") : null;

        var released =
            new[] { Date(movie, "digitalRelease"), Date(movie, "physicalRelease") }
                .OfType<DateTime>()
                .DefaultIfEmpty(DateTime.MinValue)
                .Min();

        if (Date(movie, "added") is not { } added)
        {
            return fileAdded;
        }

        var estimate = released > added ? released : added;

        return fileAdded is { } fileDate && fileDate < estimate ? fileDate : estimate;
    }

    private static Dictionary<int, string> TagNames(JsonDocument tags) =>
        tags.RootElement.EnumerateArray()
            .Where(tag => tag.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
            .ToDictionary(tag => tag.GetProperty("id").GetInt32(), tag => Text(tag, "label"));

    private async Task<JsonDocument?> GetAsync(string relative)
    {
        var settings = await _settings.GetSettingsAsync();

        if (string.IsNullOrWhiteSpace(settings.RadarrUrl) || string.IsNullOrWhiteSpace(settings.RadarrApiKey))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, settings.RadarrUrl.TrimEnd('/') + relative);
        request.Headers.Add("X-Api-Key", settings.RadarrApiKey);

        using var response = await _http.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Radarr answered {(int)response.StatusCode}.");
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static DateTime? Date(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTime.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date) &&
        date.Year > 1900
            ? date
            : null;
}
