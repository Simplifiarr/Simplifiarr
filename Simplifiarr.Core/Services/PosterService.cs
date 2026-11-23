using System.Collections.Concurrent;
using System.Text.Json;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class PosterService : IPosterService
{
    /// <summary>The largest picture that will be fetched or accepted.</summary>
    public const long MaxImageBytes = 25L * 1024 * 1024;

    private const string FanartSource = "fanart.tv";

    /// <summary>
    /// fanart.tv's names for each kind of picture, best first. A title's
    /// whole set comes in one answer, so it is kept for a while and
    /// switching between kinds doesn't ask again.
    /// </summary>
    private static readonly Dictionary<string, string[]> FanartMovie = new()
    {
        ["Primary"] = ["movieposter"],
        ["Backdrop"] = ["moviebackground"],
        ["Logo"] = ["hdmovielogo", "movielogo"],
        ["Thumb"] = ["moviethumb"],
        ["Banner"] = ["moviebanner"],
        ["Art"] = ["hdmovieclearart", "movieart"],
        ["Disc"] = ["moviedisc"]
    };

    private static readonly Dictionary<string, string[]> FanartShow = new()
    {
        ["Primary"] = ["tvposter"],
        ["Backdrop"] = ["showbackground"],
        ["Logo"] = ["hdtvlogo", "clearlogo"],
        ["Thumb"] = ["tvthumb"],
        ["Banner"] = ["tvbanner"],
        ["Art"] = ["hdclearart", "clearart"]
    };

    private static readonly Dictionary<string, string[]> FanartSeason = new()
    {
        ["Primary"] = ["seasonposter"],
        ["Thumb"] = ["seasonthumb"],
        ["Banner"] = ["seasonbanner"]
    };

    private static readonly TimeSpan FanartKeptFor = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (DateTime Fetched, string Json)> _fanart = new();

    private readonly HttpClient _httpClient;
    private readonly IJellyfinClient _jellyfin;
    private readonly ISettingsService _settings;

    public PosterService(
        HttpClient httpClient,
        IJellyfinClient jellyfin,
        ISettingsService settings)
    {
        _httpClient = httpClient;
        _jellyfin = jellyfin;
        _settings = settings;
    }

    public async Task<PosterOptions> GetOptionsAsync(
        JellyfinPosterItem item,
        string pictureType = PictureTypes.Poster)
    {
        var options = new List<PosterOption>();
        var notes = new List<string>();
        var shape = PictureTypes.Find(pictureType).Shape;

        try
        {
            options.AddRange(
                (await _jellyfin.GetRemotePostersAsync(item.Id, pictureType))
                    .Select(image =>
                        new PosterOption(
                            string.IsNullOrWhiteSpace(image.Provider) ? "Jellyfin" : image.Provider,
                            image.Url,
                            Thumbnail(image.Url, image.ThumbnailUrl, shape),
                            image.Language,
                            image.Width,
                            image.Height,
                            image.Rating)));
        }
        catch (InvalidOperationException exception)
        {
            notes.Add($"Jellyfin's own picture search didn't answer: {exception.Message}");
        }

        var settings =
            await _settings.GetSettingsAsync();

        if (!string.IsNullOrWhiteSpace(settings.FanartApiKey))
        {
            try
            {
                options.AddRange(
                    await GetFanartAsync(item, pictureType, settings.FanartApiKey.Trim()));
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                AppLog.Warn("fanart.tv picture search failed.", exception);

                notes.Add("fanart.tv couldn't be reached, or didn't accept the API key in Settings.");
            }
        }

        // The same picture can come from two sources.
        var distinct =
            options
                .GroupBy(option => option.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

        return new PosterOptions(distinct, notes);
    }

    public async Task<PictureChange> SetFromUrlAsync(
        string itemId,
        string url,
        string pictureType = PictureTypes.Poster,
        BackdropChoice backdrop = BackdropChoice.ReplaceMain)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "That isn't a web address. It should start with http:// or https://.");
        }

        byte[] bytes;

        try
        {
            using var response =
                await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The picture couldn't be fetched ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            if (response.Content.Headers.ContentLength > MaxImageBytes)
            {
                throw new InvalidOperationException("That picture is too big (over 25 MB).");
            }

            bytes = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException(
                $"The picture couldn't be fetched from {uri.Host}.",
                exception);
        }

        return await SetFromImageAsync(itemId, bytes, pictureType, backdrop);
    }

    public async Task<PictureChange> SetFromImageAsync(
        string itemId,
        byte[] image,
        string pictureType = PictureTypes.Poster,
        BackdropChoice backdrop = BackdropChoice.ReplaceMain)
    {
        if (image.LongLength > MaxImageBytes)
        {
            throw new InvalidOperationException("That picture is too big (over 25 MB).");
        }

        // Go by what the file is, not what it is called or labelled as.
        var contentType =
            ImageType(image)
            ?? throw new InvalidOperationException(
                "That isn't a picture Jellyfin can use. It needs to be a JPEG, PNG or WebP image - a link to a web page won't work, it has to be the picture itself.");

        if (pictureType != PictureTypes.Backdrop)
        {
            var previous =
                await _jellyfin.GetPosterAsync(itemId, imageType: pictureType);

            await _jellyfin.SetPosterAsync(itemId, image, contentType, pictureType);

            return new PictureChange(itemId, pictureType, previous, false, 0);
        }

        // Jellyfin always adds a backdrop to the end of the list.
        var count =
            (await _jellyfin.GetPosterItemAsync(itemId))?.Backdrops ?? 0;

        if (backdrop == BackdropChoice.Add || count == 0)
        {
            await _jellyfin.SetPosterAsync(itemId, image, contentType, pictureType);

            return new PictureChange(itemId, pictureType, null, true, count + 1);
        }

        // To change the first one: add the new one, swap it to the front,
        // then remove the old first one, which is now at the end.
        var first =
            await _jellyfin.GetPosterAsync(itemId, imageType: pictureType);

        await _jellyfin.SetPosterAsync(itemId, image, contentType, pictureType);
        await _jellyfin.SwapImagesAsync(itemId, pictureType, count, 0);
        await _jellyfin.DeleteImageAsync(itemId, pictureType, count);

        return new PictureChange(itemId, pictureType, first, false, count);
    }

    public async Task<PictureChange> SetFromItemAsync(
        string itemId,
        string sourceItemId,
        string pictureType = PictureTypes.Poster)
    {
        var source =
            await _jellyfin.GetPosterAsync(sourceItemId, imageType: pictureType)
            ?? throw new InvalidOperationException(
                $"That item has no {PictureTypes.Find(pictureType).Label.ToLowerInvariant()} to use.");

        return await SetFromImageAsync(itemId, source.Bytes, pictureType);
    }

    public async Task UndoAsync(
        PictureChange change)
    {
        if (change.Added)
        {
            await _jellyfin.DeleteImageAsync(change.ItemId, change.PictureType, change.Backdrops - 1);
        }
        else if (change.Previous is { } previous)
        {
            await SetFromImageAsync(change.ItemId, previous.Bytes, change.PictureType);
        }
        else
        {
            await _jellyfin.DeleteImageAsync(change.ItemId, change.PictureType);
        }
    }

    /// <summary>
    /// "image/jpeg", "image/png" or "image/webp" from the file's first
    /// bytes; null for anything else.
    /// </summary>
    public static string? ImageType(
        ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 8 &&
            bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (bytes.Length >= 12 &&
            bytes[..4].SequenceEqual("RIFF"u8) &&
            bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }

    private async Task<List<PosterOption>> GetFanartAsync(
        JellyfinPosterItem item,
        string pictureType,
        string apiKey)
    {
        // fanart.tv knows films (and TMDB collections) by their TMDB or
        // IMDb id, shows by TVDB id, and a season by its show and number.
        var (kind, id, names) =
            item.Type switch
            {
                "Movie" => ("movies", item.TmdbId.Length > 0 ? item.TmdbId : item.ImdbId, FanartMovie.GetValueOrDefault(pictureType)),
                "BoxSet" => ("movies", item.TmdbId, FanartMovie.GetValueOrDefault(pictureType)),
                "Series" => ("tv", item.TvdbId, FanartShow.GetValueOrDefault(pictureType)),
                "Season" => ("tv", item.SeriesTvdbId, FanartSeason.GetValueOrDefault(pictureType)),
                _ => ("", "", null)
            };

        if (id.Length == 0 || names is null)
        {
            return [];
        }

        var json =
            await GetFanartJsonAsync(kind, id, apiKey);

        if (json is null)
        {
            return [];
        }

        using var document =
            JsonDocument.Parse(json);

        var options = new List<PosterOption>();

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return options;
        }

        foreach (var name in names)
        {
            if (!document.RootElement.TryGetProperty(name, out var pictures) ||
                pictures.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var picture in pictures.EnumerateArray())
            {
                var url = Text(picture, "url");

                if (url.Length == 0)
                {
                    continue;
                }

                // Season art is for one season, or for "all" of them.
                if (item.Type == "Season" &&
                    Text(picture, "season") is { Length: > 0 } season &&
                    season != "all" &&
                    season != item.SeasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    continue;
                }

                var language = Text(picture, "lang");

                options.Add(
                    new PosterOption(
                        FanartSource,
                        url,
                        Thumbnail(url, string.Empty, "poster"),
                        language == "00" ? string.Empty : language,
                        null,
                        null,
                        null));
            }
        }

        return options;
    }

    /// <summary>
    /// fanart.tv's answer for one title, or null when it has no art for it.
    /// </summary>
    private async Task<string?> GetFanartJsonAsync(
        string kind,
        string id,
        string apiKey)
    {
        var url =
            $"https://webservice.fanart.tv/v3/{kind}/{Uri.EscapeDataString(id)}?api_key={Uri.EscapeDataString(apiKey)}";

        if (_fanart.TryGetValue(url, out var kept) &&
            DateTime.UtcNow - kept.Fetched < FanartKeptFor)
        {
            return kept.Json.Length == 0 ? null : kept.Json;
        }

        using var response =
            await _httpClient.GetAsync(url);

        // fanart.tv answers "not found" for a title it has no art for.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _fanart[url] = (DateTime.UtcNow, string.Empty);

            return null;
        }

        response.EnsureSuccessStatusCode();

        var json =
            await response.Content.ReadAsStringAsync();

        // Make sure it is JSON before keeping it.
        using (JsonDocument.Parse(json))
        {
        }

        _fanart[url] = (DateTime.UtcNow, json);

        return json;
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// A small version of a picture for showing in a grid, where the site
    /// offers one; the full picture otherwise.
    /// </summary>
    private static string Thumbnail(
        string url,
        string given,
        string shape)
    {
        if (!string.IsNullOrWhiteSpace(given))
        {
            return given;
        }

        if (url.Contains("image.tmdb.org/t/p/original/", StringComparison.OrdinalIgnoreCase))
        {
            // Wide pictures need more width to look sharp in the grid.
            var size = shape == "poster" ? "w342" : "w780";

            return url.Replace("/t/p/original/", $"/t/p/{size}/", StringComparison.OrdinalIgnoreCase);
        }

        if (url.Contains("assets.fanart.tv/fanart/", StringComparison.OrdinalIgnoreCase))
        {
            return url.Replace("/fanart/", "/preview/", StringComparison.OrdinalIgnoreCase);
        }

        return url;
    }

    private static async Task<byte[]> ReadLimitedAsync(
        Stream stream)
    {
        await using (stream)
        {
            using var buffer = new MemoryStream();

            var chunk = new byte[81920];

            int read;

            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > MaxImageBytes)
                {
                    throw new InvalidOperationException("That picture is too big (over 25 MB).");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }
}
