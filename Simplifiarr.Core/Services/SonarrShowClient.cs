using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>A show in Sonarr.</summary>
public sealed record SonarrShow(int Id, string Title, string Path);

/// <summary>What Rolling Shows needs from Sonarr.</summary>
public interface ISonarrShowClient
{
    /// <summary>Every show; empty when Sonarr isn't set up.</summary>
    Task<IReadOnlyList<SonarrShow>> GetShowsAsync();

    /// <summary>The show's episodes that have a file.</summary>
    Task<IReadOnlyList<ShowEpisode>> GetEpisodesWithFilesAsync(int seriesId);

    /// <summary>Stops Sonarr looking for these episodes. Null on success.</summary>
    Task<string?> UnmonitorAsync(IReadOnlyCollection<int> episodeIds);

    /// <summary>Asks Sonarr to look at the show's files again.</summary>
    Task RescanAsync(int seriesId);
}

public class SonarrShowClient : ISonarrShowClient
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;

    public SonarrShowClient(HttpClient http, ISettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    public async Task<IReadOnlyList<SonarrShow>> GetShowsAsync()
    {
        using var document = await GetAsync("/api/v3/series");

        if (document is null)
        {
            return [];
        }

        return document.RootElement.EnumerateArray()
            .Select(series => new SonarrShow(
                series.GetProperty("id").GetInt32(),
                Text(series, "title"),
                Text(series, "path")))
            .OrderBy(show => show.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<ShowEpisode>> GetEpisodesWithFilesAsync(int seriesId)
    {
        using var files = await GetAsync($"/api/v3/episodefile?seriesId={seriesId}");
        using var episodes = await GetAsync($"/api/v3/episode?seriesId={seriesId}");

        if (files is null || episodes is null)
        {
            return [];
        }

        var byId =
            files.RootElement.EnumerateArray()
                .ToDictionary(
                    file => file.GetProperty("id").GetInt32(),
                    file => (Path: Text(file, "path"), Size: file.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetInt64() : 0));

        var result = new List<ShowEpisode>();

        foreach (var episode in episodes.RootElement.EnumerateArray())
        {
            if (!episode.TryGetProperty("episodeFileId", out var fileId) ||
                fileId.ValueKind != JsonValueKind.Number ||
                !byId.TryGetValue(fileId.GetInt32(), out var file))
            {
                continue;
            }

            result.Add(
                new ShowEpisode(
                    episode.GetProperty("id").GetInt32(),
                    fileId.GetInt32(),
                    Number(episode, "seasonNumber"),
                    Number(episode, "episodeNumber"),
                    Text(episode, "title"),
                    episode.TryGetProperty("airDateUtc", out var aired) &&
                    aired.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(aired.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var airedUtc)
                        ? airedUtc
                        : null,
                    file.Path,
                    file.Size,
                    episode.TryGetProperty("monitored", out var monitored) && monitored.ValueKind == JsonValueKind.True));
        }

        return result;
    }

    public async Task<string?> UnmonitorAsync(IReadOnlyCollection<int> episodeIds)
    {
        if (episodeIds.Count == 0)
        {
            return null;
        }

        var body =
            new JsonObject
            {
                ["episodeIds"] = new JsonArray(episodeIds.Select(id => (JsonNode)id).ToArray()),
                ["monitored"] = false
            };

        using var response = await SendAsync(HttpMethod.Put, "/api/v3/episode/monitor", body.ToJsonString());

        return response is null
            ? "Sonarr isn't set up in Settings."
            : response.IsSuccessStatusCode
                ? null
                : $"Sonarr refused to unmonitor them ({(int)response.StatusCode}).";
    }

    public async Task RescanAsync(int seriesId)
    {
        var body = new JsonObject { ["name"] = "RescanSeries", ["seriesId"] = seriesId };

        try
        {
            using var _ = await SendAsync(HttpMethod.Post, "/api/v3/command", body.ToJsonString());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Utilities.AppLog.Warn("Couldn't ask Sonarr to rescan a show.", exception);
        }
    }

    private async Task<JsonDocument?> GetAsync(string relative)
    {
        using var response = await SendAsync(HttpMethod.Get, relative, null);

        if (response is null)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Sonarr answered {(int)response.StatusCode}.");
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string relative, string? json)
    {
        var settings = await _settings.GetSettingsAsync();

        if (string.IsNullOrWhiteSpace(settings.SonarrUrl) || string.IsNullOrWhiteSpace(settings.SonarrApiKey))
        {
            return null;
        }

        var request = new HttpRequestMessage(method, settings.SonarrUrl.TrimEnd('/') + relative);
        request.Headers.Add("X-Api-Key", settings.SonarrApiKey);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await _http.SendAsync(request);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}
