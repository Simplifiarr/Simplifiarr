using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>A movie in Radarr or a show in Sonarr, found by its folder.</summary>
public sealed record ArrItem(
    string Source,
    int Id,
    string Title,
    string Path,
    int? Year = null,
    bool Monitored = false,
    bool Upcoming = false);

/// <summary>
/// Finds which Radarr movie or Sonarr show owns a folder, and tells it the
/// folder has moved. Radarr and Sonarr are told not to move anything
/// themselves: Simplifiarr has already moved the files.
/// </summary>
public interface IArrPathService
{
    /// <summary>True when the app for this kind (movies: Radarr, TV: Sonarr) is set up.</summary>
    Task<bool> IsSetUpAsync(string kind);

    /// <summary>
    /// Null when the app isn't set up or nothing in it uses this folder.
    /// When nothing uses it exactly, an item whose own folder is gone and
    /// has the same name counts (it was moved without telling the app).
    /// </summary>
    Task<ArrItem?> FindByFolderAsync(string kind, string folderPath);

    /// <summary>Every movie (Radarr) or show (Sonarr). Empty when not set up.</summary>
    Task<IReadOnlyList<ArrItem>> GetAllAsync(string kind);

    /// <summary>The app's own root folders. Empty when not set up.</summary>
    Task<IReadOnlyList<string>> GetRootFoldersAsync(string kind);

    /// <summary>Points the item at its new folder. Null on success, otherwise why not.</summary>
    Task<string?> SetFolderAsync(string kind, ArrItem item, string newFolderPath, string newLibraryPath);

    /// <summary>Removes the item from the app without touching its files. Null on success.</summary>
    Task<string?> RemoveAsync(string kind, ArrItem item);

    /// <summary>Asks the app to look at the item's files again. Failure is not an error.</summary>
    Task RescanAsync(string kind, ArrItem item);
}

public class ArrPathService : IArrPathService
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;

    public ArrPathService(HttpClient http, ISettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    private sealed record App(string Name, string Url, string Key, string Resource, string RefreshCommand, string IdField);

    private async Task<App?> AppForAsync(string kind)
    {
        var settings = await _settings.GetSettingsAsync();

        var app =
            kind switch
            {
                StorageKinds.Movies => new App("Radarr", settings.RadarrUrl, settings.RadarrApiKey, "movie", "RefreshMovie", "movieIds"),
                StorageKinds.Tv => new App("Sonarr", settings.SonarrUrl, settings.SonarrApiKey, "series", "RefreshSeries", "seriesId"),
                _ => null
            };

        return app is null || string.IsNullOrWhiteSpace(app.Url) || string.IsNullOrWhiteSpace(app.Key)
            ? null
            : app;
    }

    public async Task<bool> IsSetUpAsync(string kind) =>
        await AppForAsync(kind) is not null;

    public async Task<ArrItem?> FindByFolderAsync(string kind, string folderPath)
    {
        var items = await GetAllAsync(kind);

        return items.FirstOrDefault(item => SamePath(item.Path, folderPath))
               ?? FindStale(items, folderPath, Directory.Exists);
    }

    /// <summary>
    /// The one item named like this folder whose own folder no longer
    /// exists, or null when there is none or more than one.
    /// </summary>
    public static ArrItem? FindStale(IEnumerable<ArrItem> items, string folderPath, Func<string, bool> folderExists)
    {
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(folderPath));

        var matches =
            items
                .Where(item => string.Equals(
                    System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(item.Path.Replace('/', '\\'))),
                    name,
                    StringComparison.OrdinalIgnoreCase))
                .Where(item => !folderExists(item.Path))
                .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    public async Task<IReadOnlyList<string>> GetRootFoldersAsync(string kind)
    {
        if (await AppForAsync(kind) is not { } app)
        {
            return [];
        }

        using var response = await SendAsync(app, HttpMethod.Get, "/api/v3/rootfolder");

        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.EnumerateArray()
            .Select(entry => entry.TryGetProperty("path", out var path) ? path.GetString() : null)
            .OfType<string>()
            .ToList();
    }

    public async Task<IReadOnlyList<ArrItem>> GetAllAsync(string kind)
    {
        if (await AppForAsync(kind) is not { } app)
        {
            return [];
        }

        using var response = await SendAsync(app, HttpMethod.Get, $"/api/v3/{app.Resource}");

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{app.Name} answered {(int)response.StatusCode} when asked for its list.");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = new List<ArrItem>();

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var path = entry.TryGetProperty("path", out var value) ? value.GetString() : null;

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            items.Add(
                new ArrItem(
                    app.Name,
                    entry.GetProperty("id").GetInt32(),
                    entry.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                    path,
                    entry.TryGetProperty("year", out var year) && year.ValueKind == JsonValueKind.Number && year.GetInt32() > 0
                        ? year.GetInt32()
                        : null,
                    entry.TryGetProperty("monitored", out var monitored) && monitored.ValueKind == JsonValueKind.True,
                    // Not out yet: there is nothing to have on disk.
                    entry.TryGetProperty("status", out var status) &&
                    status.GetString() is "tba" or "announced" or "inCinemas" or "upcoming"));
        }

        return items;
    }

    public async Task<string?> SetFolderAsync(string kind, ArrItem item, string newFolderPath, string newLibraryPath)
    {
        if (await AppForAsync(kind) is not { } app)
        {
            return "It isn't set up in Settings.";
        }

        using var current = await SendAsync(app, HttpMethod.Get, $"/api/v3/{app.Resource}/{item.Id}");

        if (!current.IsSuccessStatusCode)
        {
            return $"{app.Name} answered {(int)current.StatusCode} when asked for {item.Title}.";
        }

        var body = JsonNode.Parse(await current.Content.ReadAsStringAsync())!.AsObject();

        body["path"] = newFolderPath;
        body["rootFolderPath"] = Path.TrimEndingDirectorySeparator(newLibraryPath);

        // moveFiles=false: the files are already there.
        using var update =
            await SendAsync(
                app,
                HttpMethod.Put,
                $"/api/v3/{app.Resource}/{item.Id}?moveFiles=false",
                body.ToJsonString());

        if (!update.IsSuccessStatusCode)
        {
            var reason = await update.Content.ReadAsStringAsync();

            return $"{app.Name} refused the new folder ({(int)update.StatusCode}): {Shorten(reason)}";
        }

        return null;
    }

    public async Task<string?> RemoveAsync(string kind, ArrItem item)
    {
        if (await AppForAsync(kind) is not { } app)
        {
            return "It isn't set up in Settings.";
        }

        // Files stay: Simplifiarr sends them to the Recycle Bin itself, so
        // they can still be brought back. No import exclusion, so it can be
        // asked for again.
        using var response =
            await SendAsync(
                app,
                HttpMethod.Delete,
                $"/api/v3/{app.Resource}/{item.Id}?deleteFiles=false&addImportExclusion=false");

        return response.IsSuccessStatusCode
            ? null
            : $"{app.Name} refused to remove it ({(int)response.StatusCode}).";
    }

    public async Task RescanAsync(string kind, ArrItem item)
    {
        if (await AppForAsync(kind) is not { } app)
        {
            return;
        }

        try
        {
            var command =
                new JsonObject
                {
                    ["name"] = app.RefreshCommand,
                    [app.IdField] = app.IdField.EndsWith('s') ? new JsonArray(item.Id) : item.Id
                };

            using var _ = await SendAsync(app, HttpMethod.Post, "/api/v3/command", command.ToJsonString());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            AppLog.Warn($"Couldn't ask {app.Name} to rescan {item.Title}.", exception);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(App app, HttpMethod method, string relative, string? json = null)
    {
        var request = new HttpRequestMessage(method, app.Url.TrimEnd('/') + relative);

        request.Headers.Add("X-Api-Key", app.Key);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await _http.SendAsync(request);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(a.Replace('/', '\\')),
            Path.TrimEndingDirectorySeparator(b.Replace('/', '\\')),
            StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string text) =>
        text.Length <= 300 ? text : text[..300] + "...";
}
