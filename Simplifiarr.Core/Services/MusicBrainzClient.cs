using System.Net.Http.Headers;
using System.Text.Json;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>A release (one edition of an album) as MusicBrainz has it.</summary>
public sealed record MbRelease(
    string Id,
    string Title,
    string Artist,
    string? Year,
    string? Country,
    string? Disambiguation,
    IReadOnlyList<MbMedium> Media)
{
    public int TrackCount => Media.Sum(medium => medium.Tracks.Count);

    public string Url => $"https://musicbrainz.org/release/{Id}";
}

public sealed record MbMedium(int Position, string? Format, IReadOnlyList<MbTrack> Tracks);

public sealed record MbTrack(int Position, string Title, string Artist);

public interface IMusicBrainzClient
{
    /// <summary>Releases that might be this album, best guesses first, each with its track list.</summary>
    Task<IReadOnlyList<MbRelease>> FindReleasesAsync(string artist, string album, int trackCount, string? year, int max = 4);
}

/// <summary>
/// Asks MusicBrainz (free, no account) about albums. Its rules: no more
/// than one request a second, and a User-Agent naming the app and a way to
/// reach its maker; that is the project's GitHub page, never anything of
/// the person using Simplifiarr.
/// </summary>
public sealed class MusicBrainzClient : IMusicBrainzClient
{
    private const string Api = "https://musicbrainz.org/ws/2/";

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;

    private readonly HttpClient _http;

    public MusicBrainzClient(HttpClient http, IUpdateService updates)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Simplifiarr/{updates.CurrentVersion} ( https://github.com/{UpdateService.Repository} )");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<MbRelease>> FindReleasesAsync(string artist, string album, int trackCount, string? year, int max = 4)
    {
        var query = $"release:\"{Escape(album)}\"" + (string.IsNullOrWhiteSpace(artist) ? string.Empty : $" AND artist:\"{Escape(artist)}\"");

        using var search = await GetAsync($"release/?query={Uri.EscapeDataString(query)}&limit=25&fmt=json");

        if (search is null || !search.RootElement.TryGetProperty("releases", out var found))
        {
            return [];
        }

        // Of the good name matches, the editions that look most like the
        // folder: about as many tracks (not fewer), then the same year.
        var ids =
            found.EnumerateArray()
                .Select(release => (
                    Id: Text(release, "id"),
                    Score: release.TryGetProperty("score", out var score) && score.TryGetInt32(out var value) ? value : 0,
                    Tracks: release.TryGetProperty("track-count", out var count) && count.TryGetInt32(out var tracks) ? tracks : 0,
                    Year: Text(release, "date") is { Length: >= 4 } date ? date[..4] : null,
                    Official: Text(release, "status") == "Official"))
                .Where(release => release.Id.Length > 0 && release.Score >= 70)
                .OrderBy(release => release.Tracks >= trackCount ? release.Tracks - trackCount : (trackCount - release.Tracks) * 3)
                .ThenByDescending(release => year is not null && release.Year == year)
                .ThenByDescending(release => release.Official)
                .ThenByDescending(release => release.Score)
                .Take(max)
                .Select(release => release.Id)
                .ToList();

        var releases = new List<MbRelease>();

        foreach (var id in ids)
        {
            using var detail = await GetAsync($"release/{id}?inc=recordings+artist-credits&fmt=json");

            if (detail is not null)
            {
                releases.Add(Parse(detail.RootElement));
            }
        }

        return releases;
    }

    public static MbRelease Parse(JsonElement release)
    {
        var media =
            release.TryGetProperty("media", out var mediaList)
                ? mediaList.EnumerateArray()
                    .Select(medium => new MbMedium(
                        medium.TryGetProperty("position", out var position) && position.TryGetInt32(out var number) ? number : 1,
                        Text(medium, "format"),
                        medium.TryGetProperty("tracks", out var tracks)
                            ? tracks.EnumerateArray()
                                .Select(track => new MbTrack(
                                    track.TryGetProperty("position", out var trackPosition) && trackPosition.TryGetInt32(out var trackNumber) ? trackNumber : 0,
                                    Text(track, "title"),
                                    Credit(track) ?? Credit(release) ?? string.Empty))
                                .ToList()
                            : []))
                    .ToList()
                : [];

        var date = Text(release, "date");

        return new MbRelease(
            Text(release, "id"),
            Text(release, "title"),
            Credit(release) ?? string.Empty,
            date.Length >= 4 ? date[..4] : null,
            Text(release, "country") is { Length: > 0 } country ? country : null,
            Text(release, "disambiguation") is { Length: > 0 } note ? note : null,
            media);
    }

    // "Artist A & Artist B", the way MusicBrainz joins its credits.
    private static string? Credit(JsonElement element)
    {
        if (!element.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var text = string.Concat(credits.EnumerateArray().Select(credit => Text(credit, "name") + Text(credit, "joinphrase")));

        return text.Length > 0 ? text : null;
    }

    private async Task<JsonDocument?> GetAsync(string path)
    {
        await OneAtATime.WaitAsync();

        try
        {
            var wait = _lastRequestUtc.AddMilliseconds(1100) - DateTime.UtcNow;

            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait);
            }

            _lastRequestUtc = DateTime.UtcNow;

            using var response = await _http.GetAsync(Api + path);

            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warn($"MusicBrainz answered {(int)response.StatusCode} for {path.Split('?')[0]}.");

                return null;
            }

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            AppLog.Warn($"Couldn't reach MusicBrainz: {exception.Message}");

            return null;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    // Lucene's special characters, escaped.
    private static string Escape(string text) =>
        string.Concat(text.Select(character => "+-&|!(){}[]^\"~*?:\\/".Contains(character) ? "\\" + character : character.ToString()));

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
