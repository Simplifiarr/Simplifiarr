using System.Net;
using System.Text.Json;
using System.Web;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

/// <summary>
/// A stand-in for a Jellyfin server, answering the same web requests the
/// real one would, from memory. Lets the collection sync be tested fully
/// without ever touching a real Jellyfin.
/// </summary>
internal sealed class FakeJellyfin : HttpMessageHandler
{
    public List<JellyfinItem> LibraryItems { get; } = [];

    /// <summary>Collection id -> (name, item ids).</summary>
    public Dictionary<string, (string Name, List<string> ItemIds)> Collections { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public int CollectionLibraries { get; set; } = 1;

    /// <summary>
    /// Reproduces the old failure: every create spawns another
    /// "Collections" library.
    /// </summary>
    public bool CreatingSpawnsExtraLibrary { get; set; }

    public bool RejectKey { get; set; }

    public List<string> Writes { get; } = [];

    public JellyfinItem AddLibraryItem(string path, string type = "Movie")
    {
        var item = new JellyfinItem(Guid.NewGuid().ToString("N"), Path.GetFileName(path), type, path);

        LibraryItems.Add(item);

        return item;
    }

    public JellyfinItem AddEpisode(string path, string seriesId, string seasonId)
    {
        var item = new JellyfinItem(Guid.NewGuid().ToString("N"), Path.GetFileName(path), "Episode", path, seriesId, seasonId);

        LibraryItems.Add(item);

        return item;
    }

    public string AddCollection(string name, params string[] itemIds)
    {
        var id = Guid.NewGuid().ToString("N");

        Collections[id] = (name, itemIds.ToList());

        return id;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (RejectKey)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        var path = request.RequestUri!.AbsolutePath;
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
        var ids = (query["ids"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);

        if (request.Method != HttpMethod.Get)
        {
            Writes.Add($"{request.Method} {path}");
        }

        if (request.Method == HttpMethod.Get && path == "/Library/VirtualFolders")
        {
            var folders =
                Enumerable.Range(0, CollectionLibraries)
                    .Select(_ => new { Name = "Collections", CollectionType = "boxsets" })
                    .Cast<object>()
                    .Append(new { Name = "Movies", CollectionType = "movies" });

            return Json(folders);
        }

        if (request.Method == HttpMethod.Get && path == "/Users")
        {
            return Json(new object[]
            {
                new { Id = "kid-user", Policy = new { IsAdministrator = false } },
                new { Id = "admin-user", Policy = new { IsAdministrator = true } }
            });
        }

        if (request.Method == HttpMethod.Get && path == "/Items")
        {
            if (query["ParentId"] is { } parentId)
            {
                // Like the real thing: nothing unless asked for a user.
                return Items(
                    query["UserId"] == "admin-user" &&
                    Collections.TryGetValue(parentId, out var collection)
                        ? collection.ItemIds.Select(id => new JellyfinItem(id, id, "Movie", string.Empty))
                        : []);
            }

            if (query["IncludeItemTypes"] == "BoxSet")
            {
                return Items(
                    Collections.Select(pair => new JellyfinItem(pair.Key, pair.Value.Name, "BoxSet", string.Empty)));
            }

            var start = int.Parse(query["StartIndex"] ?? "0");
            var limit = int.Parse(query["Limit"] ?? "100000");

            // Like the real thing: unless told not to, anything already in
            // a collection is left out of the library listing.
            var listed =
                query["CollapseBoxSetItems"] == "false"
                    ? LibraryItems
                    : LibraryItems
                        .Where(item => !Collections.Values.Any(collection => collection.ItemIds.Contains(item.Id)))
                        .ToList();

            return Items(listed.Skip(start).Take(limit));
        }

        if (request.Method == HttpMethod.Post && path == "/Collections")
        {
            var id = AddCollection(query["name"]!, ids);

            if (CollectionLibraries == 0 || CreatingSpawnsExtraLibrary)
            {
                CollectionLibraries++;
            }

            return Json(new { Id = id });
        }

        if (path.StartsWith("/Collections/", StringComparison.Ordinal) && path.EndsWith("/Items", StringComparison.Ordinal))
        {
            var id = path.Split('/')[2];

            if (!Collections.TryGetValue(id, out var collection))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (request.Method == HttpMethod.Post)
            {
                collection.ItemIds.AddRange(ids.Where(itemId => !collection.ItemIds.Contains(itemId)));
            }
            else if (request.Method == HttpMethod.Delete)
            {
                collection.ItemIds.RemoveAll(ids.Contains);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static Task<HttpResponseMessage> Items(IEnumerable<JellyfinItem> items) =>
        Json(new { Items = items.Select(item => new { item.Id, item.Name, item.Type, item.Path, item.SeriesId, item.SeasonId }) });

    private static Task<HttpResponseMessage> Json(object value) =>
        Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value))
            });
}

internal sealed class JellyfinSettings : ISettingsService
{
    public SimplifiarrSettings Settings { get; } =
        new()
        {
            JellyfinUrl = "http://jellyfin.test:8096",
            JellyfinApiKey = "test-key"
        };

    public Task<SimplifiarrSettings> GetSettingsAsync() =>
        Task.FromResult(Settings);

    public Task<List<DriveProfile>> GetEnabledDriveProfilesAsync() =>
        Task.FromResult(new List<DriveProfile>());

    public Task SaveSettingsAsync(SimplifiarrSettings settings) =>
        Task.CompletedTask;
}

[TestClass]
public class CollectionSyncTests : TempFolderTest
{
    private FakeJellyfin _jellyfin = default!;
    private JellyfinSettings _settings = default!;
    private MediaCatalogService _catalog = default!;
    private CollectionService _collections = default!;
    private CollectionBuilderService _sync = default!;

    [TestInitialize]
    public void Build()
    {
        _jellyfin = new FakeJellyfin();
        _settings = new JellyfinSettings();
        _catalog = new MediaCatalogService();
        _collections = new CollectionService();

        _sync =
            new CollectionBuilderService(
                _collections,
                _catalog,
                new JellyfinClient(new HttpClient(_jellyfin), _settings));
    }

    /// <summary>A film on disk and in the catalog; optionally known to Jellyfin.</summary>
    private (MediaAsset Asset, JellyfinItem? InJellyfin) Film(
        string name,
        bool inJellyfin = true,
        string drive = "movies")
    {
        var path = WriteFile($@"{drive}\{name}\{name}.mkv");
        var asset = new MediaAsset { Title = name, MediaType = MediaType.Movie, CanonicalPath = path };

        _catalog.AddAsset(asset);

        return (asset, inJellyfin ? _jellyfin.AddLibraryItem(path) : null);
    }

    [TestMethod]
    public async Task The_first_send_creates_the_collection_in_Jellyfin_with_every_member()
    {
        var alien = Film("Alien");
        var scream = Film("Scream", drive: "other-drive-movies");
        var collection = _collections.Create("Horror");

        _collections.AddAssets(collection.Id, [alien.Asset.Id, scream.Asset.Id]);

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.IsTrue(result.CreatedInJellyfin);
        Assert.AreEqual(2, result.Added);
        Assert.IsNull(result.Warning);

        var created = _jellyfin.Collections.Values.Single();

        Assert.AreEqual("Horror", created.Name);
        CollectionAssert.AreEquivalent(
            new[] { alien.InJellyfin!.Id, scream.InJellyfin!.Id },
            created.ItemIds);

        var saved = _collections.Get(collection.Id)!;

        Assert.IsNotNull(saved.JellyfinCollectionId);
        Assert.IsNotNull(saved.LastSyncedUtc);
        Assert.HasCount(2, saved.SyncedJellyfinItemIds);
    }

    [TestMethod]
    public async Task Sending_again_with_nothing_changed_writes_nothing()
    {
        var collection = _collections.Create("Horror");

        _collections.AddAsset(collection.Id, Film("Alien").Asset.Id);

        await _sync.SyncToJellyfinAsync(collection.Id);

        _jellyfin.Writes.Clear();

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.IsFalse(result.CreatedInJellyfin);
        Assert.AreEqual(0, result.Added);
        Assert.AreEqual(1, result.AlreadyThere);
        Assert.IsEmpty(_jellyfin.Writes);
        Assert.HasCount(1, _jellyfin.Collections);
    }

    [TestMethod]
    public async Task Later_changes_are_added_and_removed_without_making_a_second_collection()
    {
        var alien = Film("Alien");
        var scream = Film("Scream");
        var collection = _collections.Create("Horror");

        _collections.AddAsset(collection.Id, alien.Asset.Id);

        await _sync.SyncToJellyfinAsync(collection.Id);

        _collections.RemoveAsset(collection.Id, alien.Asset.Id);
        _collections.AddAsset(collection.Id, scream.Asset.Id);

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.AreEqual(1, result.Added);
        Assert.AreEqual(1, result.Removed);
        Assert.HasCount(1, _jellyfin.Collections);
        CollectionAssert.AreEqual(
            new[] { scream.InJellyfin!.Id },
            _jellyfin.Collections.Values.Single().ItemIds);
    }

    [TestMethod]
    public async Task A_collection_already_in_Jellyfin_with_the_same_name_is_reused_and_its_own_items_are_kept()
    {
        var addedByHand = _jellyfin.AddLibraryItem(@"X:\Movies\Halloween\Halloween.mkv");
        var existingId = _jellyfin.AddCollection("horror", addedByHand.Id);

        var alien = Film("Alien");
        var collection = _collections.Create("Horror");

        _collections.AddAsset(collection.Id, alien.Asset.Id);

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.IsFalse(result.CreatedInJellyfin);
        Assert.AreEqual(1, result.Added);
        Assert.AreEqual(0, result.Removed);
        Assert.HasCount(1, _jellyfin.Collections);
        CollectionAssert.AreEquivalent(
            new[] { addedByHand.Id, alien.InJellyfin!.Id },
            _jellyfin.Collections[existingId].ItemIds);

        // Removing our own item later still leaves the hand-added one.
        _collections.RemoveAsset(collection.Id, alien.Asset.Id);

        await _sync.SyncToJellyfinAsync(collection.Id);

        CollectionAssert.AreEqual(
            new[] { addedByHand.Id },
            _jellyfin.Collections[existingId].ItemIds);
    }

    [TestMethod]
    public async Task An_item_Jellyfin_already_had_in_the_collection_is_not_claimed_and_so_never_removed()
    {
        var alien = Film("Alien");
        var existingId = _jellyfin.AddCollection("Horror", alien.InJellyfin!.Id);
        var collection = _collections.Create("Horror");

        _collections.AddAsset(collection.Id, alien.Asset.Id);

        var first = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.AreEqual(0, first.Added);
        Assert.AreEqual(1, first.AlreadyThere);

        _collections.RemoveAsset(collection.Id, alien.Asset.Id);

        await _sync.SyncToJellyfinAsync(collection.Id);

        // It was in Jellyfin's collection before Simplifiarr ever sent
        // anything, so it isn't Simplifiarr's to take out.
        CollectionAssert.Contains(_jellyfin.Collections[existingId].ItemIds, alien.InJellyfin.Id);
    }

    [TestMethod]
    public async Task Members_Jellyfin_does_not_have_are_reported_by_name_and_the_rest_still_go()
    {
        var alien = Film("Alien");
        var unknown = Film("Home Movie", inJellyfin: false);
        var collection = _collections.Create("Mixed");

        _collections.AddAssets(collection.Id, [alien.Asset.Id, unknown.Asset.Id]);

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.Added);
        CollectionAssert.AreEqual(new[] { "Home Movie" }, result.NotInJellyfin);
    }

    [TestMethod]
    public async Task Paths_match_whatever_way_round_the_slashes_and_capitals_are()
    {
        var path = WriteFile(@"movies\Alien\Alien.mkv");
        var asset = new MediaAsset { Title = "Alien", MediaType = MediaType.Movie, CanonicalPath = path };

        _catalog.AddAsset(asset);

        var inJellyfin = _jellyfin.AddLibraryItem(path.Replace('\\', '/').ToUpperInvariant());
        var collection = _collections.Create("Horror");

        _collections.AddAsset(collection.Id, asset.Id);

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.AreEqual(1, result.Added);
        CollectionAssert.Contains(_jellyfin.Collections.Values.Single().ItemIds, inJellyfin.Id);
    }

    [TestMethod]
    public async Task Nothing_is_created_when_Jellyfin_has_none_of_the_items()
    {
        var collection = _collections.Create("Home videos");

        _collections.AddAsset(collection.Id, Film("Holiday 2019", inJellyfin: false).Asset.Id);

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.IsFalse(result.Succeeded);
        Assert.IsEmpty(_jellyfin.Collections);
        Assert.IsEmpty(_jellyfin.Writes);
    }

    [TestMethod]
    public async Task An_empty_collection_is_not_sent()
    {
        var result = await _sync.SyncToJellyfinAsync(_collections.Create("Empty").Id);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "empty");
        Assert.IsEmpty(_jellyfin.Writes);
    }

    [TestMethod]
    public async Task It_refuses_to_create_anything_when_Jellyfin_already_has_duplicate_Collections_libraries()
    {
        _jellyfin.CollectionLibraries = 2;

        var collection = _collections.Create("Horror");

        _collections.AddAsset(collection.Id, Film("Alien").Asset.Id);

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "2 \"Collections\" libraries");
        Assert.IsEmpty(_jellyfin.Writes);
    }

    [TestMethod]
    public async Task If_creating_spawns_an_extra_library_the_user_is_warned_after_the_first_one()
    {
        // The failure that got this feature switched off before.
        _jellyfin.CreatingSpawnsExtraLibrary = true;

        var first = _collections.Create("Horror");
        var second = _collections.Create("Comedy");

        _collections.AddAsset(first.Id, Film("Alien").Asset.Id);
        _collections.AddAsset(second.Id, Film("Airplane").Asset.Id);

        var firstResult = await _sync.SyncToJellyfinAsync(first.Id);

        Assert.IsTrue(firstResult.Succeeded);
        Assert.IsNotNull(firstResult.Warning);
        StringAssert.Contains(firstResult.Warning, "2 \"Collections\" libraries");

        // And the next create is refused outright, so it stops at one.
        var secondResult = await _sync.SyncToJellyfinAsync(second.Id);

        Assert.IsFalse(secondResult.Succeeded);
        Assert.AreEqual(2, _jellyfin.CollectionLibraries);
        Assert.HasCount(1, _jellyfin.Collections);
    }

    [TestMethod]
    public async Task The_very_first_collection_on_a_fresh_Jellyfin_is_fine()
    {
        // Jellyfin makes its "Collections" library the first time one is
        // created. Going from none to one is normal, not a warning.
        _jellyfin.CollectionLibraries = 0;

        var collection = _collections.Create("Horror");

        _collections.AddAsset(collection.Id, Film("Alien").Asset.Id);

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Warning);
    }

    [TestMethod]
    public async Task A_wrong_api_key_is_explained_and_changes_nothing()
    {
        _jellyfin.RejectKey = true;

        var collection = _collections.Create("Horror");

        _collections.AddAsset(collection.Id, Film("Alien").Asset.Id);

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "API key");
        Assert.IsNull(_collections.Get(collection.Id)!.JellyfinCollectionId);
    }

    [TestMethod]
    public async Task Jellyfin_not_being_set_up_is_explained()
    {
        _settings.Settings.JellyfinApiKey = string.Empty;

        var collection = _collections.Create("Horror");

        _collections.AddAsset(collection.Id, Film("Alien").Asset.Id);

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "Settings");
    }

    [TestMethod]
    public async Task A_big_collection_is_sent_in_several_requests()
    {
        var collection = _collections.Create("Everything");

        _collections.AddAssets(
            collection.Id,
            Enumerable.Range(1, 95).Select(number => Film($"Film {number:000}").Asset.Id));

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.AreEqual(95, result.Added);
        Assert.HasCount(95, _jellyfin.Collections.Values.Single().ItemIds);
        Assert.IsGreaterThan(1, _jellyfin.Writes.Count);
    }

    [TestMethod]
    public async Task Members_whose_file_is_gone_are_counted_and_can_be_cleared_out()
    {
        var alien = Film("Alien");
        var gone = Film("Deleted Film");
        var collection = _collections.Create("Horror");

        _collections.AddAssets(collection.Id, [alien.Asset.Id, gone.Asset.Id, Guid.NewGuid()]);

        File.Delete(gone.Asset.CanonicalPath);

        Assert.AreEqual(2, await _sync.GetOrphanedMemberCountAsync(collection.Id));
        Assert.HasCount(1, await _sync.GetOrphanedMembersAsync(collection.Id));

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.AreEqual(2, result.MissingFiles);
        Assert.AreEqual(1, result.Added);

        Assert.AreEqual(2, await _sync.RemoveOrphanedMembersAsync(collection.Id));
        CollectionAssert.AreEqual(new[] { alien.Asset.Id }, _collections.Get(collection.Id)!.AssetIds);
    }

    [TestMethod]
    public async Task Sending_over_and_over_never_takes_anything_out_and_puts_it_back()
    {
        var alien = Film("Alien");
        var scream = Film("Scream");
        var collection = _collections.Create("Horror");

        _collections.AddAssets(collection.Id, [alien.Asset.Id, scream.Asset.Id]);

        await _sync.SyncToJellyfinAsync(collection.Id);

        for (var send = 2; send <= 4; send++)
        {
            _jellyfin.Writes.Clear();

            var result = await _sync.SyncToJellyfinAsync(collection.Id);

            Assert.IsTrue(result.Succeeded, result.Error);
            Assert.AreEqual(0, result.Removed, $"send {send}");
            Assert.AreEqual(0, result.Added, $"send {send}");
            Assert.AreEqual(2, result.AlreadyThere, $"send {send}");
            Assert.IsEmpty(result.NotInJellyfin);
            Assert.IsEmpty(_jellyfin.Writes);
            Assert.HasCount(2, _jellyfin.Collections.Values.Single().ItemIds);
        }
    }

    /// <summary>An episode on disk, in the catalog and in Jellyfin.</summary>
    private (MediaAsset Asset, JellyfinItem InJellyfin) Episode(
        string show,
        int season,
        int episode)
    {
        var path = WriteFile($@"tv\{show}\Season {season:00}\{show} S{season:00}E{episode:00}.mkv");

        var asset =
            new MediaAsset
            {
                Title = show,
                Season = season,
                Episode = episode,
                MediaType = MediaType.TvEpisode,
                CanonicalPath = path
            };

        _catalog.AddAsset(asset);

        return (asset, _jellyfin.AddEpisode(path, $"series-{show}", $"season-{show}-{season}"));
    }

    [TestMethod]
    public async Task A_whole_show_is_sent_as_the_show_not_as_loose_episodes()
    {
        var film = Film("Alien");

        var episodes =
            new[] { Episode("Dark", 1, 1), Episode("Dark", 1, 2), Episode("Dark", 2, 1) };

        var collection = _collections.Create("Horror");

        _collections.AddAssets(
            collection.Id,
            episodes.Select(episode => episode.Asset.Id).Append(film.Asset.Id));

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.AreEqual(2, result.Added);

        CollectionAssert.AreEquivalent(
            new[] { "series-Dark", film.InJellyfin!.Id },
            _jellyfin.Collections.Values.Single().ItemIds);
    }

    [TestMethod]
    public async Task A_whole_season_of_a_show_is_sent_as_the_season_and_odd_episodes_as_episodes()
    {
        var one = Episode("Dark", 1, 1);
        var two = Episode("Dark", 1, 2);
        var loose = Episode("Dark", 2, 1);

        Episode("Dark", 2, 2);

        var collection = _collections.Create("Favourites");

        _collections.AddAssets(collection.Id, [one.Asset.Id, two.Asset.Id, loose.Asset.Id]);

        await _sync.SyncToJellyfinAsync(collection.Id);

        CollectionAssert.AreEquivalent(
            new[] { "season-Dark-1", loose.InJellyfin.Id },
            _jellyfin.Collections.Values.Single().ItemIds);
    }

    [TestMethod]
    public async Task Episodes_sent_loose_earlier_are_swapped_for_the_show_on_the_next_send()
    {
        var one = Episode("Dark", 1, 1);
        var two = Episode("Dark", 1, 2);
        var collection = _collections.Create("Horror");

        _collections.AddAsset(collection.Id, one.Asset.Id);

        await _sync.SyncToJellyfinAsync(collection.Id);

        // Half the show: goes as an episode.
        CollectionAssert.AreEqual(
            new[] { one.InJellyfin.Id },
            _jellyfin.Collections.Values.Single().ItemIds);

        _collections.AddAsset(collection.Id, two.Asset.Id);

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.AreEqual(1, result.Added);
        Assert.AreEqual(1, result.Removed);

        CollectionAssert.AreEqual(
            new[] { "series-Dark" },
            _jellyfin.Collections.Values.Single().ItemIds);

        // And a third send has nothing left to do.
        _jellyfin.Writes.Clear();

        await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.IsEmpty(_jellyfin.Writes);
    }

    [TestMethod]
    public async Task A_film_replaced_by_a_better_copy_stays_in_the_collection()
    {
        var alien = Film("Alien");
        var collection = _collections.Create("Horror");

        _collections.AddAsset(collection.Id, alien.Asset.Id);

        // First look: remembers what the member is.
        Assert.AreEqual(0, await _sync.RefreshMembersAsync(collection.Id));

        // The file is upgraded: old one gone, new one under a new name,
        // which the catalog sees as a brand new item.
        File.Delete(alien.Asset.CanonicalPath);

        var upgradedPath = WriteFile(@"movies\Alien\Alien 2160p.mkv");
        var upgraded = new MediaAsset { Title = "Alien", MediaType = MediaType.Movie, CanonicalPath = upgradedPath };

        _catalog.AddAsset(upgraded);

        var inJellyfin = _jellyfin.AddLibraryItem(upgradedPath);

        var result = await _sync.SyncToJellyfinAsync(collection.Id);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.AreEqual(0, result.MissingFiles);
        Assert.AreEqual(1, result.Added);

        CollectionAssert.AreEqual(new[] { upgraded.Id }, _collections.Get(collection.Id)!.AssetIds);
        CollectionAssert.AreEqual(new[] { inJellyfin.Id }, _jellyfin.Collections.Values.Single().ItemIds);
        Assert.AreEqual(0, await _sync.GetOrphanedMemberCountAsync(collection.Id));
    }

    [TestMethod]
    public async Task A_missing_member_is_not_guessed_when_two_copies_could_be_it()
    {
        var alien = Film("Alien");
        var collection = _collections.Create("Horror");

        _collections.AddAsset(collection.Id, alien.Asset.Id);

        await _sync.RefreshMembersAsync(collection.Id);

        File.Delete(alien.Asset.CanonicalPath);

        Film("Alien", drive: "drive-one");
        Film("Alien", drive: "drive-two");

        Assert.AreEqual(0, await _sync.RefreshMembersAsync(collection.Id));
        CollectionAssert.AreEqual(new[] { alien.Asset.Id }, _collections.Get(collection.Id)!.AssetIds);
        Assert.AreEqual(1, await _sync.GetOrphanedMemberCountAsync(collection.Id));
    }

    [TestMethod]
    public async Task A_different_film_with_another_title_is_never_swapped_in()
    {
        var alien = Film("Alien");
        var collection = _collections.Create("Horror");

        _collections.AddAsset(collection.Id, alien.Asset.Id);

        await _sync.RefreshMembersAsync(collection.Id);

        File.Delete(alien.Asset.CanonicalPath);

        Film("Aliens");

        Assert.AreEqual(0, await _sync.RefreshMembersAsync(collection.Id));
        Assert.AreEqual(1, await _sync.GetOrphanedMemberCountAsync(collection.Id));
    }
}

[TestClass]
public class CollectionServiceTests : TempFolderTest
{
    [TestMethod]
    public void Two_collections_cannot_share_a_name()
    {
        var service = new CollectionService();

        service.Create("Horror");

        Assert.ThrowsExactly<InvalidOperationException>(() => service.Create(" horror "));
    }

    [TestMethod]
    public void Old_per_drive_collections_with_the_same_name_are_merged_into_one()
    {
        var shared = Guid.NewGuid();
        var onlyC = Guid.NewGuid();
        var onlyE = Guid.NewGuid();

        // The shape collections.json had before 2.0.
        JsonFileStore.Save(
            AppPaths.DataFile("collections.json"),
            new[]
            {
                new Collection { Name = "Horror", DriveProfileName = "E Drive", AssetIds = [shared, onlyE], CreatedUtc = new DateTime(2026, 9, 3, 18, 56, 0, DateTimeKind.Utc) },
                new Collection { Name = "Horror", DriveProfileName = "C Drive", AssetIds = [shared, onlyC], CreatedUtc = new DateTime(2026, 9, 3, 19, 0, 0, DateTimeKind.Utc) },
                new Collection { Name = "Comedy", DriveProfileName = "C Drive", AssetIds = [Guid.NewGuid()] }
            });

        var service = new CollectionService();

        Assert.HasCount(2, service.GetAll());

        var horror = service.GetAll().Single(collection => collection.Name == "Horror");

        CollectionAssert.AreEquivalent(new[] { shared, onlyE, onlyC }, horror.AssetIds);

        // And it stays merged after a restart.
        Assert.HasCount(2, new CollectionService().GetAll());
    }

    [TestMethod]
    public void Adding_the_same_item_twice_keeps_one()
    {
        var service = new CollectionService();
        var collection = service.Create("Horror");
        var item = Guid.NewGuid();

        service.AddAssets(collection.Id, [item, item]);
        service.AddAsset(collection.Id, item);

        Assert.HasCount(1, service.Get(collection.Id)!.AssetIds);
    }
}
