using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public enum SetupStatus
{
    /// <summary>Already set up the way Simplifiarr expects.</summary>
    Ok,

    /// <summary>Not yet; Simplifiarr can change it.</summary>
    CanFix,

    /// <summary>Not yet; it has to be changed by hand (the detail says how).</summary>
    Manual,

    /// <summary>Not checked: the app isn't set up, or there's nothing to compare with.</summary>
    Skipped
}

/// <summary>One thing about your apps, and whether it fits.</summary>
public sealed record SetupCheck(
    string Id,
    string App,
    string Title,
    SetupStatus Status,
    string Detail,
    string? FixLabel = null);

/// <summary>A Radarr movie or Sonarr show: where it lives and its tags.</summary>
public sealed record TaggedTitle(
    int Id,
    string Path,
    string RootFolder,
    IReadOnlyList<int> Tags);

/// <summary>Tags to take off and put on, by tag, so each title downloads to its own drive.</summary>
public sealed class RetagPlan
{
    public Dictionary<int, List<int>> Remove { get; } = [];

    public Dictionary<int, List<int>> Add { get; } = [];

    /// <summary>Titles that change.</summary>
    public int Titles { get; set; }

    /// <summary>Titles on a drive no download client saves to: left alone.</summary>
    public int NoClientForDrive { get; set; }
}

/// <summary>
/// Checks that the torrent client, Radarr, Sonarr and Seerr are set up the
/// way Simplifiarr expects, and changes what it safely can:
///
///   - new downloads land on the downloads drive, and new titles are added
///     to a library there (so every import is a hardlink);
///   - each title already on another drive downloads to that drive, by its
///     tag (so its new episodes and upgrades are hardlinked too);
///   - Radarr and Sonarr hardlink rather than copy.
///
/// Nothing that would move existing torrents is changed: that is left to you.
/// </summary>
public interface IStackSetupService
{
    Task<List<SetupCheck>> CheckAsync();

    /// <summary>Makes one change from <see cref="CheckAsync"/>. Null on success, otherwise why not.</summary>
    Task<string?> FixAsync(string id);

    /// <summary>
    /// After a title moved to another drive: gives it the tag that makes its
    /// downloads land there. Returns a note for the move's message, or null
    /// when nothing changed.
    /// </summary>
    Task<string?> FollowDriveAsync(string kind, int itemId, string newFolderPath);
}

public sealed class StackSetupService : IStackSetupService
{
    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ITorrentClient _client;

    public StackSetupService(HttpClient http, ISettingsService settings, ITorrentClient client)
    {
        _http = http;
        _settings = settings;
        _client = client;
    }

    private sealed record ArrApp(string Kind, string Name, string Url, string Key, string Resource, string CategoryField, string IdsField);

    private static ArrApp? AppOf(SimplifiarrSettings settings, string kind) =>
        kind == StorageKinds.Tv
            ? Configured(settings.SonarrUrl, settings.SonarrApiKey) ? new ArrApp(kind, "Sonarr", settings.SonarrUrl, settings.SonarrApiKey, "series", "tvCategory", "seriesIds") : null
            : Configured(settings.RadarrUrl, settings.RadarrApiKey) ? new ArrApp(kind, "Radarr", settings.RadarrUrl, settings.RadarrApiKey, "movie", "movieCategory", "movieIds") : null;

    private static bool Configured(string url, string key) =>
        !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key);

    // ---------------------------------------------------------------- check

    public async Task<List<SetupCheck>> CheckAsync()
    {
        var settings = await _settings.GetSettingsAsync();
        var downloadsRoot = StorageService.DownloadsDriveRoot(settings);
        var layout = await _client.GetLayoutAsync();
        var checks = new List<SetupCheck>();

        if (downloadsRoot is null)
        {
            checks.Add(new SetupCheck(
                "downloads-drive", "Simplifiarr", "A downloads drive is picked", SetupStatus.Manual,
                "Pick the drive new downloads land on under Settings, Drives. Most checks here follow from it."));
        }

        checks.Add(TorrentClientCheck(settings, downloadsRoot, layout));

        foreach (var kind in new[] { StorageKinds.Movies, StorageKinds.Tv })
        {
            if (AppOf(settings, kind) is not { } app)
            {
                checks.Add(new SetupCheck($"{Key(kind)}-setup", kind == StorageKinds.Tv ? "Sonarr" : "Radarr", "Connected", SetupStatus.Skipped, "Not set up in Settings, so not checked."));
                continue;
            }

            try
            {
                checks.AddRange(await ArrChecksAsync(settings, app, downloadsRoot, layout));
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                checks.Add(new SetupCheck($"{Key(kind)}-setup", app.Name, "Reachable", SetupStatus.Manual, $"Couldn't ask {app.Name}: {exception.Message}"));
            }
        }

        if (Configured(settings.SeerrUrl, settings.SeerrApiKey))
        {
            foreach (var kind in new[] { StorageKinds.Movies, StorageKinds.Tv })
            {
                try
                {
                    checks.Add(await SeerrCheckAsync(settings, kind, downloadsRoot));
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
                {
                    checks.Add(new SetupCheck($"seerr-{Key(kind)}", "Seerr", "Reachable", SetupStatus.Manual, $"Couldn't ask Seerr: {exception.Message}"));
                }
            }
        }
        else
        {
            checks.Add(new SetupCheck("seerr-setup", "Seerr", "Connected", SetupStatus.Skipped, "Not set up in Settings, so not checked."));
        }

        return checks;
    }

    private SetupCheck TorrentClientCheck(SimplifiarrSettings settings, string? downloadsRoot, TorrentClientLayout? layout)
    {
        const string Title = "New torrents are saved on the downloads drive";

        if (downloadsRoot is null)
        {
            return new SetupCheck("client-save", _client.Name, Title, SetupStatus.Skipped, "No downloads drive picked yet.");
        }

        if (layout is null)
        {
            return new SetupCheck("client-save", _client.Name, Title, SetupStatus.Manual, $"Couldn't read {_client.Name}'s settings. Check its address in Settings.");
        }

        if (SameDrive(layout.DefaultSavePath, downloadsRoot))
        {
            return new SetupCheck("client-save", _client.Name, Title, SetupStatus.Ok, $"Saves to {layout.DefaultSavePath}.");
        }

        return DownloadsFolder(settings) is { } folder
            ? new SetupCheck("client-save", _client.Name, Title, SetupStatus.CanFix,
                $"Saves to {layout.DefaultSavePath}. It should save to {folder}. Torrents already there stay where they are.", $"Save to {folder}")
            : new SetupCheck("client-save", _client.Name, Title, SetupStatus.Manual,
                $"Saves to {layout.DefaultSavePath}. Set the downloads drive's downloads folder in Settings, then come back.");
    }

    private async Task<List<SetupCheck>> ArrChecksAsync(SimplifiarrSettings settings, ArrApp app, string? downloadsRoot, TorrentClientLayout? layout)
    {
        var checks = new List<SetupCheck>();
        var key = Key(app.Kind);

        // Hardlinks rather than copies.
        var media = await GetAsync(app, "/api/v3/config/mediamanagement") as JsonObject;
        var hardlinks = media?["copyUsingHardlinks"]?.GetValue<bool>() == true;

        checks.Add(
            hardlinks
                ? new SetupCheck($"{key}-hardlinks", app.Name, "Imports are hardlinked, not copied", SetupStatus.Ok, "\"Use Hardlinks instead of Copy\" is on.")
                : new SetupCheck($"{key}-hardlinks", app.Name, "Imports are hardlinked, not copied", SetupStatus.CanFix,
                    "\"Use Hardlinks instead of Copy\" is off, so every import takes the space twice.", "Turn it on"));

        // A library on the downloads drive, for new titles.
        var roots = await RootFoldersAsync(app);

        if (downloadsRoot is not null)
        {
            var library = LibraryOn(settings, app.Kind);

            checks.Add(
                roots.Any(root => SameDrive(root, downloadsRoot))
                    ? new SetupCheck($"{key}-root", app.Name, "Has a library on the downloads drive", SetupStatus.Ok,
                        $"{roots.First(root => SameDrive(root, downloadsRoot))}.")
                    : library is not null
                        ? new SetupCheck($"{key}-root", app.Name, "Has a library on the downloads drive", SetupStatus.CanFix,
                            $"None of its root folders is on {downloadsRoot}, so new titles can't be added there.", $"Add {library}")
                        : new SetupCheck($"{key}-root", app.Name, "Has a library on the downloads drive", SetupStatus.Manual,
                            $"None of its root folders is on {downloadsRoot}. Set the downloads drive's {app.Kind} library in Settings first."));
        }

        // The download client titles without a drive tag use.
        var clients = await ClientsAsync(app);
        var untagged = clients.Where(client => client.Tags.Count == 0).ToList();

        if (layout is null)
        {
            checks.Add(new SetupCheck($"{key}-client", app.Name, "Downloads land where they should", SetupStatus.Manual, $"Couldn't read {_client.Name}'s categories."));
            return checks;
        }

        var defaultDrive = downloadsRoot ?? untagged.Select(client => RootOf(layout.FolderOf(client.Category))).FirstOrDefault();

        if (downloadsRoot is not null)
        {
            var wrong = untagged.Where(client => !SameDrive(layout.FolderOf(client.Category), downloadsRoot)).ToList();

            checks.Add(
                untagged.Count == 0
                    ? new SetupCheck($"{key}-client", app.Name, "New titles download to the downloads drive", SetupStatus.Manual,
                        $"Every {_client.Name} download client in {app.Name} has a tag, so a title without one can't download. Take the tags off one whose category saves to {downloadsRoot}.")
                    : wrong.Count == 0
                        ? new SetupCheck($"{key}-client", app.Name, "New titles download to the downloads drive", SetupStatus.Ok,
                            $"\"{untagged[0].Name}\" saves to {layout.FolderOf(untagged[0].Category)}.")
                        : new SetupCheck($"{key}-client", app.Name, "New titles download to the downloads drive", SetupStatus.Manual,
                            $"\"{wrong[0].Name}\" (no tags) uses the category \"{wrong[0].Category}\", which saves to {layout.FolderOf(wrong[0].Category)}. " +
                            $"In {_client.Name}, point that category at a folder on {downloadsRoot}. Simplifiarr doesn't change it: torrents in that category would move with it."));
        }

        // Each title downloads to the drive it lives on.
        if (defaultDrive is not null)
        {
            var routing = Routing(clients, layout);
            var plan = PlanRetag(await TitlesAsync(app), routing, defaultDrive);
            var tagWord = app.Kind == StorageKinds.Tv ? "show" : "movie";

            checks.Add(
                plan.Titles == 0
                    ? new SetupCheck($"{key}-tags", app.Name, "Each title downloads to its own drive", SetupStatus.Ok,
                        $"Every {tagWord}'s downloads land on the drive it lives on, so they're hardlinked." +
                        (plan.NoClientForDrive > 0 ? $" {plan.NoClientForDrive} live on a drive no download client saves to." : string.Empty))
                    : new SetupCheck($"{key}-tags", app.Name, "Each title downloads to its own drive", SetupStatus.CanFix,
                        $"{plan.Titles} {tagWord}(s) download to a different drive than they live on, so their new episodes and upgrades are copied instead of linked. " +
                        "Their drive tags are changed to match; nothing on disk moves.",
                        $"Retag {plan.Titles}"));
        }

        return checks;
    }

    private async Task<SetupCheck> SeerrCheckAsync(SimplifiarrSettings settings, string kind, string? downloadsRoot)
    {
        var key = Key(kind);
        var name = kind == StorageKinds.Tv ? "Sonarr" : "Radarr";
        var title = $"New {(kind == StorageKinds.Tv ? "shows" : "movies")} requested in Seerr land on the downloads drive";

        if (downloadsRoot is null)
        {
            return new SetupCheck($"seerr-{key}", "Seerr", title, SetupStatus.Skipped, "No downloads drive picked yet.");
        }

        var server = (await SeerrServersAsync(settings, kind)).FirstOrDefault(IsDefaultServer);

        if (server is null)
        {
            return new SetupCheck($"seerr-{key}", "Seerr", title, SetupStatus.Skipped, $"Seerr has no default {name} server.");
        }

        var current = server["activeDirectory"]?.GetValue<string>() ?? string.Empty;

        if (SameDrive(current, downloadsRoot))
        {
            return new SetupCheck($"seerr-{key}", "Seerr", title, SetupStatus.Ok, $"Its default {name} server adds them to {current}.");
        }

        var target = await SeerrTargetAsync(settings, kind, downloadsRoot);

        return target is null
            ? new SetupCheck($"seerr-{key}", "Seerr", title, SetupStatus.Manual,
                $"Its default {name} server adds them to {current}. Give {name} a root folder on {downloadsRoot} first (above).")
            : new SetupCheck($"seerr-{key}", "Seerr", title, SetupStatus.CanFix,
                $"Its default {name} server adds them to {current}, so they download to {downloadsRoot} and get copied there.", $"Use {target}");
    }

    // ------------------------------------------------------------------ fix

    public async Task<string?> FixAsync(string id)
    {
        var settings = await _settings.GetSettingsAsync();
        var downloadsRoot = StorageService.DownloadsDriveRoot(settings);

        try
        {
            if (id == "client-save")
            {
                return DownloadsFolder(settings) is { } folder
                    ? await LogResult(await _client.SetDefaultSavePathAsync(folder), $"{_client.Name} now saves new torrents to {folder}.")
                    : "Set the downloads drive's downloads folder in Settings first.";
            }

            var parts = id.Split('-', 2);
            var kind = parts.Length == 2 && parts[1] == "tv" || parts[0] == "tv" ? StorageKinds.Tv : StorageKinds.Movies;

            if (id.StartsWith("seerr-", StringComparison.Ordinal))
            {
                return await FixSeerrAsync(settings, kind, downloadsRoot);
            }

            if (AppOf(settings, kind) is not { } app)
            {
                return "That app isn't set up in Settings.";
            }

            return parts[1] switch
            {
                "hardlinks" => await FixHardlinksAsync(app),
                "root" => await FixRootAsync(settings, app),
                "tags" => await FixTagsAsync(app, downloadsRoot),
                _ => "That can't be changed from here."
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            AppLog.Warn($"Setup: couldn't change {id}.", exception);

            return $"It didn't work: {exception.Message}";
        }
    }

    private static Task<string?> LogResult(string? problem, string done)
    {
        if (problem is null)
        {
            AppLog.Info($"Setup: {done}");
        }

        return Task.FromResult(problem);
    }

    private async Task<string?> FixHardlinksAsync(ArrApp app)
    {
        if (await GetAsync(app, "/api/v3/config/mediamanagement") is not JsonObject media)
        {
            return $"Couldn't read {app.Name}'s media management settings.";
        }

        media["copyUsingHardlinks"] = true;

        return await LogResult(
            await SendAsync(app, HttpMethod.Put, $"/api/v3/config/mediamanagement/{media["id"]}", media),
            $"{app.Name} now hardlinks imports.");
    }

    private async Task<string?> FixRootAsync(SimplifiarrSettings settings, ArrApp app)
    {
        if (LibraryOn(settings, app.Kind) is not { } library)
        {
            return $"Set the downloads drive's {app.Kind} library in Settings first.";
        }

        Directory.CreateDirectory(library);

        return await LogResult(
            await SendAsync(app, HttpMethod.Post, "/api/v3/rootfolder", new JsonObject { ["path"] = library }),
            $"{app.Name} has a new root folder: {library}.");
    }

    private async Task<string?> FixTagsAsync(ArrApp app, string? downloadsRoot)
    {
        if (await _client.GetLayoutAsync() is not { } layout)
        {
            return $"Couldn't read {_client.Name}'s categories.";
        }

        var clients = await ClientsAsync(app);
        var defaultDrive = downloadsRoot ?? clients.Where(client => client.Tags.Count == 0).Select(client => RootOf(layout.FolderOf(client.Category))).FirstOrDefault();

        if (defaultDrive is null)
        {
            return "There's no download client without tags to compare with.";
        }

        var plan = PlanRetag(await TitlesAsync(app), Routing(clients, layout), defaultDrive);

        return await LogResult(await ApplyAsync(app, plan), $"{app.Name}: {plan.Titles} title(s) retagged to download to their own drive.");
    }

    private async Task<string?> ApplyAsync(ArrApp app, RetagPlan plan)
    {
        foreach (var (tag, ids) in plan.Remove)
        {
            if (await SendAsync(app, HttpMethod.Put, $"/api/v3/{app.Resource}/editor", Editor(app, ids, tag, "remove")) is { } problem)
            {
                return problem;
            }
        }

        foreach (var (tag, ids) in plan.Add)
        {
            if (await SendAsync(app, HttpMethod.Put, $"/api/v3/{app.Resource}/editor", Editor(app, ids, tag, "add")) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    private static JsonObject Editor(ArrApp app, List<int> ids, int tag, string how) =>
        new()
        {
            [app.IdsField] = new JsonArray(ids.Select(id => (JsonNode)id).ToArray()),
            ["tags"] = new JsonArray(tag),
            ["applyTags"] = how
        };

    private async Task<string?> FixSeerrAsync(SimplifiarrSettings settings, string kind, string? downloadsRoot)
    {
        if (downloadsRoot is null)
        {
            return "Pick a downloads drive in Settings first.";
        }

        var server = (await SeerrServersAsync(settings, kind)).FirstOrDefault(IsDefaultServer);
        var target = await SeerrTargetAsync(settings, kind, downloadsRoot);

        if (server is null || target is null)
        {
            return "There's nothing to point it at yet.";
        }

        var id = server["id"]!.GetValue<int>();

        // Seerr takes the server back without its id, which is read-only.
        server.Remove("id");
        server["activeDirectory"] = target;

        var resource = kind == StorageKinds.Tv ? "sonarr" : "radarr";

        return await LogResult(
            await SeerrSendAsync(settings, HttpMethod.Put, $"/api/v1/settings/{resource}/{id}", server),
            $"Seerr's default {resource} server now adds to {target}.");
    }

    // --------------------------------------------------------- follow drive

    public async Task<string?> FollowDriveAsync(string kind, int itemId, string newFolderPath)
    {
        try
        {
            var settings = await _settings.GetSettingsAsync();

            if (AppOf(settings, kind) is not { } app || await _client.GetLayoutAsync() is not { } layout)
            {
                return null;
            }

            var clients = await ClientsAsync(app);
            var defaultDrive =
                StorageService.DownloadsDriveRoot(settings) ??
                clients.Where(client => client.Tags.Count == 0).Select(client => RootOf(layout.FolderOf(client.Category))).FirstOrDefault();

            if (defaultDrive is null || await GetAsync(app, $"/api/v3/{app.Resource}/{itemId}") is not JsonObject item)
            {
                return null;
            }

            var title =
                new TaggedTitle(
                    itemId,
                    newFolderPath,
                    Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(newFolderPath)) ?? string.Empty,
                    TagsOf(item));

            // The other titles tell which of two tags for one drive to use.
            var others = (await TitlesAsync(app)).Where(other => other.Id != itemId).ToList();
            var plan = PlanRetag([.. others, title], Routing(clients, layout), defaultDrive, onlyId: itemId);

            if (plan.Titles == 0)
            {
                return null;
            }

            if (await ApplyAsync(app, plan) is { } problem)
            {
                AppLog.Warn($"Couldn't change {app.Name}'s tags after a move: {problem}");
                return null;
            }

            return $"Its {app.Name} tag now sends future downloads to {RootOf(newFolderPath)}.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            AppLog.Warn("Couldn't update tags after a move.", exception);
            return null;
        }
    }

    // ----------------------------------------------------------- planning

    /// <summary>
    /// The tags that make each title download to the drive it lives on.
    /// Titles on <paramref name="defaultDrive"/> need none (the client
    /// without tags saves there). Elsewhere, a title keeps a tag that already
    /// fits; otherwise it gets the one most titles in its root folder use for
    /// that drive. A drive no client saves to is left alone.
    /// </summary>
    public static RetagPlan PlanRetag(
        IReadOnlyList<TaggedTitle> titles,
        IReadOnlyDictionary<int, string> routing,
        string defaultDrive,
        int? onlyId = null)
    {
        var plan = new RetagPlan();

        var used =
            titles
                .SelectMany(title => title.Tags
                    .Where(tag => routing.TryGetValue(tag, out var drive) && SameDrive(drive, title.Path))
                    .Select(tag => (Root: Normalize(title.RootFolder), Tag: tag)))
                .GroupBy(entry => entry.Root, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.GroupBy(entry => entry.Tag).OrderByDescending(tags => tags.Count()).Select(tags => tags.Key).ToList(),
                    StringComparer.OrdinalIgnoreCase);

        foreach (var title in titles.Where(title => onlyId is null || title.Id == onlyId))
        {
            var have = title.Tags.Where(routing.ContainsKey).ToList();
            int? want;

            if (SameDrive(title.Path, defaultDrive))
            {
                want = null;
            }
            else
            {
                var candidates = routing.Where(pair => SameDrive(pair.Value, title.Path)).Select(pair => pair.Key).OrderBy(tag => tag).ToList();

                if (candidates.Count == 0)
                {
                    plan.NoClientForDrive++;
                    continue;
                }

                want =
                    have.Where(candidates.Contains).Select(tag => (int?)tag).FirstOrDefault() ??
                    used.GetValueOrDefault(Normalize(title.RootFolder))?.Where(candidates.Contains).Select(tag => (int?)tag).FirstOrDefault() ??
                    candidates[0];
            }

            var wrong = have.Where(tag => tag != want).ToList();
            var missing = want is { } needed && !have.Contains(needed);

            if (wrong.Count == 0 && !missing)
            {
                continue;
            }

            plan.Titles++;

            foreach (var tag in wrong)
            {
                Into(plan.Remove, tag).Add(title.Id);
            }

            if (missing)
            {
                Into(plan.Add, want!.Value).Add(title.Id);
            }
        }

        return plan;
    }

    private static List<int> Into(Dictionary<int, List<int>> map, int tag)
    {
        if (!map.TryGetValue(tag, out var ids))
        {
            ids = [];
            map[tag] = ids;
        }

        return ids;
    }

    private sealed record DownloadClient(string Name, string Category, IReadOnlyList<int> Tags);

    // Tag -> the drive its download client saves to.
    private static Dictionary<int, string> Routing(IEnumerable<DownloadClient> clients, TorrentClientLayout layout)
    {
        var routing = new Dictionary<int, string>();

        foreach (var client in clients)
        {
            foreach (var tag in client.Tags)
            {
                routing.TryAdd(tag, RootOf(layout.FolderOf(client.Category)));
            }
        }

        return routing;
    }

    // ------------------------------------------------------------- reading

    private async Task<List<DownloadClient>> ClientsAsync(ArrApp app)
    {
        var clients = new List<DownloadClient>();

        foreach (var node in (await GetAsync(app, "/api/v3/downloadclient") as JsonArray) ?? [])
        {
            if (node is not JsonObject client ||
                client["enable"]?.GetValue<bool>() != true ||
                !string.Equals(client["implementation"]?.GetValue<string>(), "QBittorrent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var category =
                (client["fields"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .FirstOrDefault(field => field["name"]?.GetValue<string>() == app.CategoryField)?["value"]?.GetValue<string>() ?? string.Empty;

            clients.Add(new DownloadClient(client["name"]?.GetValue<string>() ?? "?", category, TagsOf(client)));
        }

        return clients;
    }

    private async Task<List<TaggedTitle>> TitlesAsync(ArrApp app) =>
        ((await GetAsync(app, $"/api/v3/{app.Resource}") as JsonArray) ?? [])
            .OfType<JsonObject>()
            .Where(item => !string.IsNullOrWhiteSpace(item["path"]?.GetValue<string>()))
            .Select(item => new TaggedTitle(
                item["id"]!.GetValue<int>(),
                item["path"]!.GetValue<string>(),
                item["rootFolderPath"]?.GetValue<string>() ?? Path.GetDirectoryName(item["path"]!.GetValue<string>()) ?? string.Empty,
                TagsOf(item)))
            .ToList();

    private async Task<List<string>> RootFoldersAsync(ArrApp app) =>
        ((await GetAsync(app, "/api/v3/rootfolder") as JsonArray) ?? [])
            .OfType<JsonObject>()
            .Select(root => root["path"]?.GetValue<string>())
            .OfType<string>()
            .ToList();

    private static List<int> TagsOf(JsonObject item) =>
        (item["tags"] as JsonArray ?? []).Select(tag => tag!.GetValue<int>()).ToList();

    private async Task<List<JsonObject>> SeerrServersAsync(SimplifiarrSettings settings, string kind) =>
        ((await SeerrGetAsync(settings, $"/api/v1/settings/{(kind == StorageKinds.Tv ? "sonarr" : "radarr")}") as JsonArray) ?? [])
            .OfType<JsonObject>()
            .ToList();

    private static bool IsDefaultServer(JsonObject server) =>
        server["isDefault"]?.GetValue<bool>() == true && server["is4k"]?.GetValue<bool>() != true;

    // The app's root folder on the downloads drive Seerr should add to:
    // the downloads drive's library when it is one of them.
    private async Task<string?> SeerrTargetAsync(SimplifiarrSettings settings, string kind, string downloadsRoot)
    {
        if (AppOf(settings, kind) is not { } app)
        {
            return null;
        }

        var roots = (await RootFoldersAsync(app)).Where(root => SameDrive(root, downloadsRoot)).ToList();
        var library = LibraryOn(settings, kind);

        return roots.FirstOrDefault(root => library is not null && SamePath(root, library)) ?? roots.FirstOrDefault();
    }

    // ---------------------------------------------------------------- http

    private async Task<JsonNode?> GetAsync(ArrApp app, string relative)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Url.TrimEnd('/') + relative);
        request.Headers.Add("X-Api-Key", app.Key);

        using var response = await _http.SendAsync(request);

        response.EnsureSuccessStatusCode();

        return JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<string?> SendAsync(ArrApp app, HttpMethod method, string relative, JsonNode body)
    {
        using var request = new HttpRequestMessage(method, app.Url.TrimEnd('/') + relative);
        request.Headers.Add("X-Api-Key", app.Key);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request);

        return response.IsSuccessStatusCode
            ? null
            : $"{app.Name} answered {(int)response.StatusCode}: {Shorten(await response.Content.ReadAsStringAsync())}";
    }

    private async Task<JsonNode?> SeerrGetAsync(SimplifiarrSettings settings, string relative)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.SeerrUrl.TrimEnd('/') + relative);
        request.Headers.Add("X-Api-Key", settings.SeerrApiKey);

        using var response = await _http.SendAsync(request);

        response.EnsureSuccessStatusCode();

        return JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<string?> SeerrSendAsync(SimplifiarrSettings settings, HttpMethod method, string relative, JsonNode body)
    {
        using var request = new HttpRequestMessage(method, settings.SeerrUrl.TrimEnd('/') + relative);
        request.Headers.Add("X-Api-Key", settings.SeerrApiKey);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request);

        return response.IsSuccessStatusCode
            ? null
            : $"Seerr answered {(int)response.StatusCode}: {Shorten(await response.Content.ReadAsStringAsync())}";
    }

    // ------------------------------------------------------------- helpers

    private static string Key(string kind) => kind == StorageKinds.Tv ? "tv" : "movies";

    private static string? DownloadsFolder(SimplifiarrSettings settings) =>
        DownloadsProfile(settings) is { DownloadPath.Length: > 0 } profile ? profile.DownloadPath : null;

    private static string? LibraryOn(SimplifiarrSettings settings, string kind) =>
        DownloadsProfile(settings) is { } profile
            ? kind == StorageKinds.Tv
                ? NullIfBlank(profile.TvLibraryPath)
                : NullIfBlank(profile.MovieLibraryPath)
            : null;

    private static DriveProfile? DownloadsProfile(SimplifiarrSettings settings) =>
        settings.DriveProfiles.FirstOrDefault(profile =>
            profile.Enabled && string.Equals(profile.Name, settings.DownloadsDrive, StringComparison.OrdinalIgnoreCase));

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>"C:\" for any path on drive C.</summary>
    public static string RootOf(string path)
    {
        try
        {
            return (Path.GetPathRoot(Path.GetFullPath(path.Replace('/', '\\'))) ?? path).ToUpperInvariant();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool SameDrive(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(RootOf(a), RootOf(b), StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(path.Replace('/', '\\'));

    private static string Shorten(string text) =>
        text.Length <= 300 ? text : text[..300] + "...";
}
