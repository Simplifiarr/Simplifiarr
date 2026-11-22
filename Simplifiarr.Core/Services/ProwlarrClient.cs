using System.Text.Json;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class ProwlarrClient : IProwlarrClient
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public ProwlarrClient(
        HttpClient httpClient,
        ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    public async Task<List<ProwlarrSearchResult>> SearchAsync(
        string query,
        int? categoryId = null)
    {
        try
        {
            var relativePath =
                $"/api/v1/search?query={Uri.EscapeDataString(query)}&type=search";

            if (categoryId.HasValue)
            {
                relativePath += $"&categories={categoryId.Value}";
            }

            var json =
                await SendAsync(relativePath);

            if (json is null)
            {
                return [];
            }

            using var document =
                JsonDocument.Parse(json);

            var results =
                new List<ProwlarrSearchResult>();

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var protocol = GetString(item, "protocol");

                if (!string.Equals(protocol, "torrent", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(new ProwlarrSearchResult
                {
                    Title = GetString(item, "title"),
                    Indexer = GetString(item, "indexer"),
                    SizeBytes = GetLong(item, "size"),
                    Seeders = GetInt(item, "seeders"),
                    Leechers = GetInt(item, "leechers"),
                    PublishDate = GetNullableDateTime(item, "publishDate"),
                    DownloadUrl = GetString(item, "downloadUrl"),
                    InfoUrl = GetString(item, "infoUrl"),
                    Protocol = protocol
                });
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    public async Task<ReleaseSource?> GetReleaseAsync(string downloadUrl)
    {
        if (downloadUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return new ReleaseSource(null, downloadUrl);
        }

        try
        {
            var (bytes, magnet) = await FetchAsync(downloadUrl);

            if (magnet is not null)
            {
                return new ReleaseSource(null, magnet);
            }

            if (bytes is null)
            {
                return null;
            }

            // Some indexers send the magnet link as the page itself.
            var start = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 7));

            return start.Equals("magnet:", StringComparison.OrdinalIgnoreCase)
                ? new ReleaseSource(null, System.Text.Encoding.UTF8.GetString(bytes).Trim())
                : new ReleaseSource(bytes, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }

    public async Task<byte[]?> DownloadTorrentAsync(
        string downloadUrl)
    {
        try
        {
            return (await FetchAsync(downloadUrl)).Bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches a download link, following web redirects by hand: the
    /// automatic ones break on a redirect to a magnet link. Returns the
    /// file, or the magnet link it pointed to.
    /// </summary>
    private async Task<(byte[]? Bytes, string? Magnet)> FetchAsync(string url)
    {
        var current = new Uri(url);

        for (var hop = 0; hop < 6; hop++)
        {
            using var response = await _httpClient.GetAsync(current);

            var status = (int)response.StatusCode;

            if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var target = location.OriginalString;

                if (target.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, target);
                }

                current = location.IsAbsoluteUri ? location : new Uri(current, location);

                continue;
            }

            return response.IsSuccessStatusCode
                ? (await response.Content.ReadAsByteArrayAsync(), null)
                : (null, null);
        }

        return (null, null);
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            var json =
                await SendAsync("/api/v1/system/status");

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

        if (string.IsNullOrWhiteSpace(settings.ProwlarrUrl) ||
            string.IsNullOrWhiteSpace(settings.ProwlarrApiKey))
        {
            return null;
        }

        if (!Uri.TryCreate(
                settings.ProwlarrUrl.TrimEnd('/') + relativePath,
                UriKind.Absolute,
                out var uri))
        {
            return null;
        }

        using var request =
            new HttpRequestMessage(HttpMethod.Get, uri);

        request.Headers.Add("X-Api-Key", settings.ProwlarrApiKey);

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

    private static long GetLong(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number
            ? property.GetInt64()
            : 0;
    }

    private static DateTime? GetNullableDateTime(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               DateTime.TryParse(property.GetString(), out var value)
            ? value
            : null;
    }
}
