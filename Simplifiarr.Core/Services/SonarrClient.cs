using System.Net.Http;
using System.Text.Json;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class SonarrClient : ISonarrClient
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public SonarrClient(
        HttpClient httpClient,
        ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    public async Task<List<SonarrEpisodeRecord>> GetEpisodesAsync()
    {
        try
        {
            var seriesJson =
                await SendAsync("/api/v3/series");

            if (seriesJson is null)
            {
                return [];
            }

            using var seriesDocument =
                JsonDocument.Parse(seriesJson);

            var records =
                new List<SonarrEpisodeRecord>();

            foreach (var series in seriesDocument.RootElement.EnumerateArray())
            {
                var seriesId =
                    GetInt(series, "id");

                var tvdbId =
                    GetInt(series, "tvdbId");

                var title =
                    GetString(series, "title");

                var year =
                    GetNullableInt(series, "year");

                var genres =
                    GetStringArray(series, "genres");

                var qualityProfileId =
                    GetInt(series, "qualityProfileId");

                var monitored =
                    GetBool(series, "monitored");

                var episodeFilesJson =
                    await SendAsync(
                        $"/api/v3/episodefile?seriesId={seriesId}");

                if (episodeFilesJson is null)
                {
                    continue;
                }

                var episodesByFileId =
                    new Dictionary<int, (string Title, int? EpisodeNumber)>();

                var episodesJson =
                    await SendAsync(
                        $"/api/v3/episode?seriesId={seriesId}");

                if (episodesJson is not null)
                {
                    using var episodesDocument =
                        JsonDocument.Parse(episodesJson);

                    foreach (var episode in episodesDocument.RootElement.EnumerateArray())
                    {
                        var episodeFileId =
                            GetInt(episode, "episodeFileId");

                        if (episodeFileId <= 0)
                        {
                            continue;
                        }

                        episodesByFileId[episodeFileId] =
                            (GetString(episode, "title"),
                             GetNullableInt(episode, "episodeNumber"));
                    }
                }

                using var episodeFilesDocument =
                    JsonDocument.Parse(episodeFilesJson);

                foreach (var episodeFile in episodeFilesDocument.RootElement.EnumerateArray())
                {
                    var episodeFileRecordId =
                        GetInt(episodeFile, "id");

                    episodesByFileId.TryGetValue(
                        episodeFileRecordId,
                        out var episodeInfo);

                    records.Add(new SonarrEpisodeRecord
                    {
                        TvdbId = tvdbId,
                        SeriesTitle = title,
                        Year = year,
                        Genres = genres,
                        QualityProfileId = qualityProfileId,
                        Monitored = monitored,
                        FilePath = GetString(episodeFile, "path"),
                        SeasonNumber = GetNullableInt(episodeFile, "seasonNumber"),
                        EpisodeNumber = episodeInfo.EpisodeNumber,
                        EpisodeTitle =
                            string.IsNullOrWhiteSpace(episodeInfo.Title)
                                ? null
                                : episodeInfo.Title
                    });
                }
            }

            return records;
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

            if (!root.TryGetProperty("parsedEpisodeInfo", out var parsed) ||
                parsed.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var episodes =
                parsed.TryGetProperty("episodeNumbers", out var numbers) && numbers.ValueKind == JsonValueKind.Array
                    ? numbers.EnumerateArray().Where(number => number.ValueKind == JsonValueKind.Number).Select(number => number.GetInt32()).ToList()
                    : [];

            var identity =
                new ArrIdentity
                {
                    Source = "Sonarr",
                    Title = GetString(parsed, "seriesTitle"),
                    Season = GetNullableInt(parsed, "seasonNumber"),
                    Episode = episodes.Count > 0 ? episodes[0] : null
                };

            // Sonarr has this show: its own title, year and folder win.
            if (root.TryGetProperty("series", out var series) &&
                series.ValueKind == JsonValueKind.Object)
            {
                var path = GetString(series, "path");

                identity = identity with
                {
                    Title = GetString(series, "title") is { Length: > 0 } title ? title : identity.Title,
                    Year = GetNullableInt(series, "year") is > 0 and var year ? year : null,
                    KnownFolder = string.IsNullOrWhiteSpace(path) ? null : path,
                    UsesSeasonFolders = !series.TryGetProperty("seasonFolder", out var seasonFolder) || seasonFolder.ValueKind != JsonValueKind.False,
                    TvdbId = GetNullableInt(series, "tvdbId"),
                    ImdbId = GetString(series, "imdbId") is { Length: > 0 } imdb ? imdb : null
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
            var root = document.RootElement;
            var defaults = new ArrFolderFormats();

            return defaults with
            {
                SeriesFolderFormat = GetString(root, "seriesFolderFormat") is { Length: > 0 } series ? series : defaults.SeriesFolderFormat,
                SeasonFolderFormat = GetString(root, "seasonFolderFormat") is { Length: > 0 } season ? season : defaults.SeasonFolderFormat,
                SpecialsFolderFormat = GetString(root, "specialsFolderFormat") is { Length: > 0 } specials ? specials : defaults.SpecialsFolderFormat
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

        if (string.IsNullOrWhiteSpace(settings.SonarrUrl) ||
            string.IsNullOrWhiteSpace(settings.SonarrApiKey))
        {
            return null;
        }

        if (!Uri.TryCreate(
                settings.SonarrUrl.TrimEnd('/') + relativePath,
                UriKind.Absolute,
                out var uri))
        {
            return null;
        }

        using var request =
            new HttpRequestMessage(HttpMethod.Get, uri);

        request.Headers.Add("X-Api-Key", settings.SonarrApiKey);

        using var response =
            await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync();
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
