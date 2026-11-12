using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// qBittorrent's Web API (v2). Works with or without a login: with
/// "Bypass authentication for clients on localhost" on, no username is
/// needed; otherwise Simplifiarr logs in and logs in again whenever the
/// session runs out.
/// </summary>
public class QbittorrentClient : ITorrentClient
{
    // One login per address and user, shared by every request.
    private static readonly ConcurrentDictionary<string, string> Sessions = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;

    public QbittorrentClient(
        HttpClient httpClient,
        ISettingsService settingsService)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
    }

    public string Name => "qBittorrent";

    // Tested on qBittorrent 5.3: pointing a torrent at a folder that
    // already holds its files keeps them, moves the rest, and needs a
    // recheck afterwards.
    public TorrentClientCapabilities Capabilities { get; } =
        new()
        {
            CanExportTorrentFiles = true,
            CanAddTorrents = true,
            CanRelocate = true,
            RelocateKeepsExistingFiles = true,
            HasCategories = true
        };

    // ------------------------------------------------------------ reading

    public async Task<List<TorrentRecord>> GetTorrentsAsync() =>
        await TryGetTorrentsAsync() ?? [];

    public async Task<List<TorrentRecord>?> TryGetTorrentsAsync()
    {
        try
        {
            var json = await GetStringAsync("/api/v2/torrents/info");

            if (json is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(json);

            return document.RootElement
                .EnumerateArray()
                .Select(torrent =>
                {
                    var state = GetString(torrent, "state");

                    return new TorrentRecord
                    {
                        TorrentHash = GetString(torrent, "hash"),
                        Name = GetString(torrent, "name"),
                        Category = GetString(torrent, "category"),
                        SavePath = GetString(torrent, "save_path"),
                        ContentPath = GetString(torrent, "content_path"),
                        State = state,
                        Status = MapState(state),
                        SizeBytes = GetLong(torrent, "size"),
                        Progress = torrent.TryGetProperty("progress", out var progress) && progress.ValueKind == JsonValueKind.Number
                            ? progress.GetDouble()
                            : 0,
                        Tracker = GetString(torrent, "tracker"),
                        InfoHashV1 = GetString(torrent, "infohash_v1"),
                        InfoHashV2 = GetString(torrent, "infohash_v2")
                    };
                })
                .ToList();
        }
        catch (Exception exception) when (IsConnectionProblem(exception))
        {
            return null;
        }
    }

    public async Task<byte[]?> GetTorrentExportBytesAsync(
        string torrentHash)
    {
        try
        {
            using var response =
                await SendAsync(HttpMethod.Get, $"/api/v2/torrents/export?hash={Uri.EscapeDataString(torrentHash)}");

            return response is { IsSuccessStatusCode: true }
                ? await response.Content.ReadAsByteArrayAsync()
                : null;
        }
        catch (Exception exception) when (IsConnectionProblem(exception))
        {
            return null;
        }
    }

    public async Task<List<TorrentContentFile>?> GetTorrentFilesAsync(
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

            using var document = JsonDocument.Parse(json);

            return document.RootElement
                .EnumerateArray()
                .Select(file =>
                    new TorrentContentFile(
                        // qBittorrent always uses "/" here.
                        GetString(file, "name").Replace('/', Path.DirectorySeparatorChar),
                        GetLong(file, "size")))
                .ToList();
        }
        catch (Exception exception) when (IsConnectionProblem(exception))
        {
            return null;
        }
    }

    /// <summary>qBittorrent's states in Simplifiarr's words.</summary>
    public static TorrentStatus MapState(
        string state) =>
        state switch
        {
            "uploading" or "stalledUP" or "forcedUP" => TorrentStatus.Seeding,
            "downloading" or "stalledDL" or "forcedDL" => TorrentStatus.Downloading,
            // qBittorrent 5 says "stopped", 4 said "paused".
            "stoppedUP" or "stoppedDL" or "pausedUP" or "pausedDL" => TorrentStatus.Stopped,
            "queuedUP" or "queuedDL" => TorrentStatus.Queued,
            "metaDL" or "forcedMetaDL" or "allocating" => TorrentStatus.Preparing,
            "checkingUP" or "checkingDL" or "checkingResumeData" => TorrentStatus.Checking,
            "moving" => TorrentStatus.Moving,
            "error" or "missingFiles" => TorrentStatus.Error,
            _ => TorrentStatus.Unknown
        };

    // ------------------------------------------------------------ changing

    public async Task<TorrentAddResult> AddTorrentAsync(
        byte[] torrentBytes,
        string savePath)
    {
        try
        {
            using var response =
                await SendAsync(HttpMethod.Post, "/api/v2/torrents/add", () =>
                {
                    var content = new MultipartFormDataContent();

                    var file = new ByteArrayContent(torrentBytes);
                    file.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");

                    content.Add(file, "torrents", "cross-seed.torrent");
                    content.Add(new StringContent(savePath), "savepath");

                    // qBittorrent renamed "paused" to "stopped" in v5 - send
                    // both so this works with either. An unknown field is
                    // simply ignored.
                    content.Add(new StringContent("true"), "paused");
                    content.Add(new StringContent("true"), "stopped");
                    content.Add(new StringContent("false"), "skip_checking");

                    return content;
                });

            if (response is null)
            {
                return new TorrentAddResult { Success = false, ErrorMessage = "qBittorrent's address isn't set (see Settings)." };
            }

            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return new TorrentAddResult
                {
                    Success = false,
                    ErrorMessage = $"qBittorrent returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}".Trim()
                };
            }

            // qBittorrent answers 200 "Fails." when it refuses a torrent -
            // usually because the same torrent is already in it.
            return body.Trim().Equals("Fails.", StringComparison.OrdinalIgnoreCase)
                ? new TorrentAddResult { Success = false, ErrorMessage = "qBittorrent refused the torrent (usually because it's already in the client)." }
                : new TorrentAddResult { Success = true };
        }
        catch (Exception exception) when (IsConnectionProblem(exception))
        {
            return new TorrentAddResult { Success = false, ErrorMessage = exception.Message };
        }
    }

    // qBittorrent 5 renamed pause/resume to stop/start; try the new name
    // first and fall back for older versions.
    public Task<string?> StopTorrentAsync(
        string torrentHash) =>
        PostFormAsync(["/api/v2/torrents/stop", "/api/v2/torrents/pause"], new() { ["hashes"] = torrentHash });

    public Task<string?> StartTorrentAsync(
        string torrentHash) =>
        PostFormAsync(["/api/v2/torrents/start", "/api/v2/torrents/resume"], new() { ["hashes"] = torrentHash });

    public Task<string?> SetLocationAsync(
        string torrentHash,
        string location) =>
        PostFormAsync(["/api/v2/torrents/setLocation"], new() { ["hashes"] = torrentHash, ["location"] = location });

    public Task<string?> RecheckTorrentAsync(
        string torrentHash) =>
        PostFormAsync(["/api/v2/torrents/recheck"], new() { ["hashes"] = torrentHash });

    private async Task<string?> PostFormAsync(
        string[] paths,
        Dictionary<string, string> form)
    {
        try
        {
            foreach (var path in paths)
            {
                using var response =
                    await SendAsync(HttpMethod.Post, path, () => new FormUrlEncodedContent(form));

                if (response is null)
                {
                    return "qBittorrent's address isn't set (see Settings).";
                }

                // An older qBittorrent doesn't know the newer name.
                if (response.StatusCode == HttpStatusCode.NotFound && path != paths[^1])
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
        catch (Exception exception) when (IsConnectionProblem(exception))
        {
            return $"Couldn't reach qBittorrent: {exception.Message}";
        }
    }

    // ----------------------------------------------------------- connecting

    public async Task<string?> TestConnectionAsync(
        string url,
        string username,
        string password)
    {
        if (!TryGetBaseUri(url, out var baseUri))
        {
            return "That isn't a valid address. It should look like http://localhost:8080";
        }

        try
        {
            var connection = new Connection(baseUri, username, password);

            // Always try the login given, never a session remembered from
            // an earlier one.
            if (connection.HasLogin && !await LogInAsync(connection))
            {
                return "qBittorrent didn't accept that username and password.";
            }

            using var response = await SendOnceAsync(connection, HttpMethod.Get, "/api/v2/app/version", null);

            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return string.IsNullOrWhiteSpace(username)
                    ? "qBittorrent asks for a login. Enter its Web UI username and password, or turn on \"Bypass authentication for clients on localhost\" in its Web UI options."
                    : "qBittorrent didn't accept that username and password.";
            }

            return $"qBittorrent answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Check the address is its Web UI.";
        }
        catch (Exception exception) when (IsConnectionProblem(exception))
        {
            return $"Couldn't reach qBittorrent at that address: {exception.Message}";
        }
    }

    private sealed record Connection(Uri BaseUri, string Username, string Password)
    {
        public string SessionKey => $"{BaseUri}|{Username}";

        public bool HasLogin => !string.IsNullOrWhiteSpace(Username);
    }

    private async Task<string?> GetStringAsync(
        string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path);

        if (response is null)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Sends with the saved settings; null when no address is set.</summary>
    private async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method,
        string path,
        Func<HttpContent>? content = null)
    {
        var settings = await _settingsService.GetSettingsAsync();

        if (!TryGetBaseUri(settings.TorrentClientUrl, out var baseUri))
        {
            return null;
        }

        return await SendAsync(
            new Connection(baseUri, settings.TorrentClientUsername, settings.TorrentClientPassword),
            method,
            path,
            content);
    }

    // Sends with the current session; if qBittorrent says "log in first"
    // and there's a login to give, logs in and sends once more.
    private async Task<HttpResponseMessage> SendAsync(
        Connection connection,
        HttpMethod method,
        string path,
        Func<HttpContent>? content)
    {
        var response = await SendOnceAsync(connection, method, path, content);

        if (response.StatusCode != HttpStatusCode.Forbidden || !connection.HasLogin)
        {
            return response;
        }

        response.Dispose();

        await LogInAsync(connection);

        return await SendOnceAsync(connection, method, path, content);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        Connection connection,
        HttpMethod method,
        string path,
        Func<HttpContent>? content)
    {
        // Relative to the address, so one behind a reverse proxy
        // (http://server/qbittorrent/) keeps its folder.
        var request = new HttpRequestMessage(method, new Uri(connection.BaseUri, path.TrimStart('/')));

        if (content is not null)
        {
            request.Content = content();
        }

        if (Sessions.TryGetValue(connection.SessionKey, out var session))
        {
            request.Headers.Add("Cookie", $"SID={session}");
        }

        using (request)
        {
            return await _httpClient.SendAsync(request);
        }
    }

    /// <summary>True when qBittorrent accepted the login.</summary>
    private async Task<bool> LogInAsync(
        Connection connection)
    {
        Sessions.TryRemove(connection.SessionKey, out _);

        using var request =
            new HttpRequestMessage(HttpMethod.Post, new Uri(connection.BaseUri, "api/v2/auth/login"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = connection.Username,
                    ["password"] = connection.Password
                })
            };

        // qBittorrent checks the request comes from its own address.
        request.Headers.Referrer = connection.BaseUri;

        using var response = await _httpClient.SendAsync(request);

        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return false;
        }

        foreach (var cookie in cookies)
        {
            var pair = cookie.Split(';', 2)[0].Trim();

            if (pair.StartsWith("SID=", StringComparison.OrdinalIgnoreCase))
            {
                Sessions[connection.SessionKey] = pair[4..];

                return true;
            }
        }

        return false;
    }

    private static bool TryGetBaseUri(
        string? url,
        out Uri baseUri)
    {
        baseUri = null!;

        return !string.IsNullOrWhiteSpace(url) &&
               Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out baseUri!) &&
               baseUri.Scheme is "http" or "https";
    }

    private static bool IsConnectionProblem(
        Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or IOException;

    private static string GetString(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static long GetLong(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt64()
            : 0;
}
