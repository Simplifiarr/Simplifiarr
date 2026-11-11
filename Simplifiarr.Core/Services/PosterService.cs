using System.Text.Json;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class PosterService : IPosterService
{
    /// <summary>The largest picture that will be fetched or accepted.</summary>
    public const long MaxImageBytes = 25L * 1024 * 1024;

    private const string FanartSource = "fanart.tv";

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
        JellyfinPosterItem item)
    {
        var options = new List<PosterOption>();
        var notes = new List<string>();

        try
        {
            options.AddRange(
                (await _jellyfin.GetRemotePostersAsync(item.Id))
                    .Select(image =>
                        new PosterOption(
                            string.IsNullOrWhiteSpace(image.Provider) ? "Jellyfin" : image.Provider,
                            image.Url,
                            Thumbnail(image.Url, image.ThumbnailUrl),
                            image.Language,
                            image.Width,
                            image.Height,
                            image.Rating)));
        }
        catch (InvalidOperationException exception)
        {
            notes.Add($"Jellyfin's own poster search didn't answer: {exception.Message}");
        }

        var settings =
            await _settings.GetSettingsAsync();

        if (!string.IsNullOrWhiteSpace(settings.FanartApiKey) &&
            item.Type is "Movie" or "Series")
        {
            try
            {
                options.AddRange(
                    await GetFanartAsync(item, settings.FanartApiKey.Trim()));
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                AppLog.Warn("fanart.tv poster search failed.", exception);

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

    public async Task<JellyfinImage?> SetFromUrlAsync(
        string itemId,
        string url)
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

        return await SetFromImageAsync(itemId, bytes);
    }

    public async Task<JellyfinImage?> SetFromImageAsync(
        string itemId,
        byte[] image)
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

        var previous =
            await _jellyfin.GetPosterAsync(itemId);

        await _jellyfin.SetPosterAsync(itemId, image, contentType);

        return previous;
    }

    public async Task<JellyfinImage?> SetFromItemAsync(
        string itemId,
        string sourceItemId)
    {
        var source =
            await _jellyfin.GetPosterAsync(sourceItemId)
            ?? throw new InvalidOperationException("That item has no poster to use.");

        return await SetFromImageAsync(itemId, source.Bytes);
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
        string apiKey)
    {
        // fanart.tv knows films by their TMDB or IMDb id, shows by TVDB id.
        var (kind, id, property) =
            item.Type == "Movie"
                ? ("movies", item.TmdbId.Length > 0 ? item.TmdbId : item.ImdbId, "movieposter")
                : ("tv", item.TvdbId, "tvposter");

        if (id.Length == 0)
        {
            return [];
        }

        using var response =
            await _httpClient.GetAsync(
                $"https://webservice.fanart.tv/v3/{kind}/{Uri.EscapeDataString(id)}?api_key={Uri.EscapeDataString(apiKey)}");

        // fanart.tv answers "not found" for a title it has no art for.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();

        using var document =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var options = new List<PosterOption>();

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty(property, out var posters) ||
            posters.ValueKind != JsonValueKind.Array)
        {
            return options;
        }

        foreach (var poster in posters.EnumerateArray())
        {
            var url =
                poster.TryGetProperty("url", out var urlValue) &&
                urlValue.ValueKind == JsonValueKind.String
                    ? urlValue.GetString() ?? string.Empty
                    : string.Empty;

            if (url.Length == 0)
            {
                continue;
            }

            var language =
                poster.TryGetProperty("lang", out var lang) &&
                lang.ValueKind == JsonValueKind.String
                    ? lang.GetString() ?? string.Empty
                    : string.Empty;

            options.Add(
                new PosterOption(
                    FanartSource,
                    url,
                    Thumbnail(url, string.Empty),
                    language == "00" ? string.Empty : language,
                    null,
                    null,
                    null));
        }

        return options;
    }

    /// <summary>
    /// A small version of a poster for showing in a grid, where the site
    /// offers one; the full picture otherwise.
    /// </summary>
    private static string Thumbnail(
        string url,
        string given)
    {
        if (!string.IsNullOrWhiteSpace(given))
        {
            return given;
        }

        if (url.Contains("image.tmdb.org/t/p/original/", StringComparison.OrdinalIgnoreCase))
        {
            return url.Replace("/t/p/original/", "/t/p/w342/", StringComparison.OrdinalIgnoreCase);
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
