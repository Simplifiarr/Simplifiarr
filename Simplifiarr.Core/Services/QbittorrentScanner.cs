using System.Net.Http.Headers;
using System.Text.Json;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class QbittorrentScanner : IQbittorrentScanner
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public QbittorrentScanner(
        HttpClient httpClient,
        ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    public async Task<List<TorrentRecord>> GetTorrentsAsync() =>
        await TryGetTorrentsAsync() ?? [];

    public async Task<List<TorrentRecord>?> TryGetTorrentsAsync()
    {
        try
        {
            var json =
                await GetStringAsync("/api/v2/torrents/info");

            if (json is null)
            {
                return null;
            }

            using var document =
                JsonDocument.Parse(json);

            var torrents =
                new List<TorrentRecord>();

            foreach (var torrent in document.RootElement.EnumerateArray())
            {
                torrents.Add(new TorrentRecord
                {
                    TorrentHash = GetString(torrent, "hash"),
                    Name = GetString(torrent, "name"),
                    Category = GetString(torrent, "category"),
                    SavePath = GetString(torrent, "save_path"),
                    ContentPath = GetString(torrent, "content_path"),
                    State = GetString(torrent, "state"),
                    SizeBytes = GetLong(torrent, "size"),
                    Progress = torrent.TryGetProperty("progress", out var progress) && progress.ValueKind == JsonValueKind.Number
                        ? progress.GetDouble()
                        : 0,
                    Tracker = GetString(torrent, "tracker"),
                    InfoHashV1 = GetString(torrent, "infohash_v1"),
                    InfoHashV2 = GetString(torrent, "infohash_v2")
                });
            }

            return torrents;
        }
        catch
        {
            return null;
        }
    }

    public async Task<byte[]?> GetTorrentExportBytesAsync(
        string torrentHash)
    {
        try
        {
            var baseUri =
                await GetBaseUriAsync();

            if (baseUri is null)
            {
                return null;
            }

            return await _httpClient.GetByteArrayAsync(
                new Uri(baseUri, $"/api/v2/torrents/export?hash={torrentHash}"));
        }
        catch
        {
            return null;
        }
    }

    public async Task<QbittorrentAddResult> AddTorrentAsync(
        byte[] torrentBytes,
        string savePath)
    {
        try
        {
            var baseUri =
                await GetBaseUriAsync();

            if (baseUri is null)
            {
                return new QbittorrentAddResult
                {
                    Success = false,
                    ErrorMessage = "qBittorrent URL is not configured (check Settings)."
                };
            }

            using var content =
                new MultipartFormDataContent();

            var fileContent =
                new ByteArrayContent(torrentBytes);

            fileContent.Headers.ContentType =
                new MediaTypeHeaderValue("application/x-bittorrent");

            content.Add(fileContent, "torrents", "cross-seed.torrent");
            content.Add(new StringContent(savePath), "savepath");

            // qBittorrent renamed "paused" to "stopped" in v5 - send both so
            // this works regardless of the user's client version. An
            // unrecognized field is harmless; qBittorrent just ignores it.
            content.Add(new StringContent("true"), "paused");
            content.Add(new StringContent("true"), "stopped");
            content.Add(new StringContent("false"), "skip_checking");

            using var response =
                await _httpClient.PostAsync(
                    new Uri(baseUri, "/api/v2/torrents/add"),
                    content);

            var body =
                await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                // qBittorrent answers 200 "Fails." when it refuses a torrent -
                // most commonly because the same info-hash is already in the
                // client. That is not a successful add.
                if (body.Trim().Equals("Fails.", StringComparison.OrdinalIgnoreCase))
                {
                    return new QbittorrentAddResult
                    {
                        Success = false,
                        ErrorMessage =
                            "qBittorrent refused the torrent (usually because it's already in the client)."
                    };
                }

                return new QbittorrentAddResult { Success = true };
            }

            return new QbittorrentAddResult
            {
                Success = false,
                ErrorMessage =
                    $"qBittorrent returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body} " +
                    "(if your instance requires login, adding torrents isn't supported yet.)"
            };
        }
        catch (Exception ex)
        {
            return new QbittorrentAddResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<List<QbittorrentFile>?> GetTorrentFilesAsync(
        string torrentHash)
    {
        try
        {
            var json =
                await GetStringAsync($"/api/v2/torrents/files?hash={Uri.EscapeDataString(torrentHash)}");

            if (json is null)
            {
                return null;
            }

            using var document =
                JsonDocument.Parse(json);

            return document.RootElement
                .EnumerateArray()
                .Select(file =>
                    new QbittorrentFile(
                        // qBittorrent always uses "/" here.
                        GetString(file, "name").Replace('/', Path.DirectorySeparatorChar),
                        GetLong(file, "size")))
                .ToList();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    // qBittorrent 5 renamed pause/resume to stop/start; try the new name
    // first and fall back for older versions.
    public Task<string?> StopTorrentAsync(
        string torrentHash) =>
        PostAsync(["/api/v2/torrents/stop", "/api/v2/torrents/pause"], new() { ["hashes"] = torrentHash });

    public Task<string?> StartTorrentAsync(
        string torrentHash) =>
        PostAsync(["/api/v2/torrents/start", "/api/v2/torrents/resume"], new() { ["hashes"] = torrentHash });

    public Task<string?> SetLocationAsync(
        string torrentHash,
        string location) =>
        PostAsync(["/api/v2/torrents/setLocation"], new() { ["hashes"] = torrentHash, ["location"] = location });

    public Task<string?> RecheckTorrentAsync(
        string torrentHash) =>
        PostAsync(["/api/v2/torrents/recheck"], new() { ["hashes"] = torrentHash });

    private async Task<string?> PostAsync(
        string[] paths,
        Dictionary<string, string> form)
    {
        var baseUri =
            await GetBaseUriAsync();

        if (baseUri is null)
        {
            return "qBittorrent's URL is not set (see Settings).";
        }

        try
        {
            foreach (var path in paths)
            {
                using var content = new FormUrlEncodedContent(form);
                using var response = await _httpClient.PostAsync(new Uri(baseUri, path), content);

                // An older qBittorrent doesn't know the newer name.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound && path != paths[^1])
                {
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync();

                return $"qBittorrent returned HTTP {(int)response.StatusCode} {response.ReasonPhrase} {body}".Trim();
            }

            return "qBittorrent didn't recognise the request.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return $"Couldn't reach qBittorrent: {exception.Message}";
        }
    }

    private async Task<Uri?> GetBaseUriAsync()
    {
        var settings =
            await _settingsService.GetSettingsAsync();

        if (string.IsNullOrWhiteSpace(settings.QbittorrentUrl))
        {
            return null;
        }

        return Uri.TryCreate(settings.QbittorrentUrl.TrimEnd('/'), UriKind.Absolute, out var baseUri)
            ? baseUri
            : null;
    }

    private async Task<string?> GetStringAsync(
        string relativePath)
    {
        var baseUri =
            await GetBaseUriAsync();

        if (baseUri is null)
        {
            return null;
        }

        return await _httpClient.GetStringAsync(new Uri(baseUri, relativePath));
    }

    private static string GetString(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(
            propertyName,
            out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static long GetLong(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(
            propertyName,
            out var property)
            ? property.GetInt64()
            : 0;
    }
}
