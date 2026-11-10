using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Sends collections to Jellyfin as real Jellyfin collections.
///
/// History, so the same mistakes aren't made twice. Before 2.0 a
/// collection was a folder of hardlinks that Jellyfin was pointed at as an
/// extra library. That forced one collection per drive (hardlinks can't
/// cross drives) and showed every film twice in Jellyfin. An attempt to
/// use Jellyfin's own collections instead was abandoned after Jellyfin
/// ended up with duplicate "Collections" libraries.
///
/// What is different now:
///   - A collection is only a list, so it spans drives; nothing is linked
///     or copied.
///   - Members are matched to Jellyfin's items by file path, which both
///     sides agree on exactly.
///   - An existing Jellyfin collection of the same name is reused. A new
///     one is created at most once per sync, never when Jellyfin already
///     has more than one "Collections" library, and the count is checked
///     again straight afterwards so a repeat of the old problem is caught
///     at one, not many.
///   - Only items Simplifiarr added are ever removed.
/// </summary>
public class CollectionBuilderService
    : ICollectionBuilderService
{
    private readonly ICollectionService _collectionService;
    private readonly IMediaCatalogService _catalogService;
    private readonly IJellyfinClient _jellyfin;

    // One sync at a time: two running together could each decide Jellyfin
    // has no such collection yet and both create it.
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public CollectionBuilderService(
        ICollectionService collectionService,
        IMediaCatalogService catalogService,
        IJellyfinClient jellyfin)
    {
        _collectionService = collectionService;
        _catalogService = catalogService;
        _jellyfin = jellyfin;
    }

    public async Task<CollectionSyncResult> SyncToJellyfinAsync(
        Guid collectionId)
    {
        await _syncLock.WaitAsync();

        try
        {
            return await SyncCoreAsync(collectionId);
        }
        catch (Exception exception)
        {
            AppLog.Error("Sending a collection to Jellyfin failed.", exception);

            return new CollectionSyncResult { Error = exception.Message };
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<CollectionSyncResult> SyncCoreAsync(
        Guid collectionId)
    {
        var result = new CollectionSyncResult();

        // Pick up anything renamed or replaced since the last look, so it
        // is sent rather than reported as missing.
        await RefreshMembersAsync(collectionId);

        var collection = _collectionService.Get(collectionId);

        if (collection is null)
        {
            result.Error = "That collection no longer exists.";

            return result;
        }

        // 1. Which of this collection's members does Jellyfin have?
        var jellyfinIdByPath =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Every episode Jellyfin has, by show and by season, so a whole
        // show or season can be sent as one thing.
        var episodesBySeries = new Dictionary<string, HashSet<string>>();
        var episodesBySeason = new Dictionary<string, HashSet<string>>();
        var seasonsBySeries = new Dictionary<string, HashSet<string>>();

        foreach (var item in await _jellyfin.GetLibraryItemsAsync())
        {
            jellyfinIdByPath.TryAdd(NormalizePath(item.Path), item.Id);

            if (item.SeriesId.Length == 0)
            {
                continue;
            }

            Bucket(episodesBySeries, item.SeriesId).Add(item.Id);

            if (item.SeasonId.Length > 0)
            {
                Bucket(episodesBySeason, item.SeasonId).Add(item.Id);
                Bucket(seasonsBySeries, item.SeriesId).Add(item.SeasonId);
            }
        }

        var wantedIds = new List<string>();

        foreach (var assetId in collection.AssetIds)
        {
            var asset = _catalogService.GetAsset(assetId);

            if (asset is null || !File.Exists(asset.CanonicalPath))
            {
                result.MissingFiles++;

                continue;
            }

            if (jellyfinIdByPath.TryGetValue(NormalizePath(asset.CanonicalPath), out var jellyfinId))
            {
                wantedIds.Add(jellyfinId);
            }
            else
            {
                result.NotInJellyfin.Add(Describe(asset));
            }
        }

        wantedIds =
            GroupEpisodes(
                wantedIds.Distinct().ToList(),
                episodesBySeries,
                episodesBySeason,
                seasonsBySeries);

        // 2. Find Jellyfin's collection: the one sent to before if it is
        //    still there, otherwise one with the same name.
        var existing = await _jellyfin.GetCollectionsAsync();

        var jellyfinCollection =
            existing.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(collection.JellyfinCollectionId) &&
                string.Equals(item.Id, collection.JellyfinCollectionId, StringComparison.OrdinalIgnoreCase))
            ??
            existing.FirstOrDefault(item =>
                string.Equals(item.Name.Trim(), collection.Name.Trim(), StringComparison.OrdinalIgnoreCase));

        var previouslySynced =
            collection.SyncedJellyfinItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (jellyfinCollection is null)
        {
            if (wantedIds.Count == 0)
            {
                result.Error =
                    collection.AssetIds.Count == 0
                        ? "This collection is empty, so there is nothing to send yet."
                        : "Jellyfin doesn't have any of this collection's items in its libraries, so no collection was made. " +
                          "Check that these folders are in a Jellyfin library and that Jellyfin has scanned them.";

                return result;
            }

            var librariesBefore = await _jellyfin.CountCollectionLibrariesAsync();

            if (librariesBefore > 1)
            {
                result.Error =
                    $"Jellyfin has {librariesBefore} \"Collections\" libraries; it should have one. " +
                    "Nothing was sent, because creating a collection now could make it worse. " +
                    "In Jellyfin's Dashboard, under Libraries, remove the extra ones first.";

                return result;
            }

            var newId =
                await _jellyfin.CreateCollectionAsync(collection.Name.Trim(), wantedIds);

            result.CreatedInJellyfin = true;
            result.Added = wantedIds.Count;

            _collectionService.SetJellyfinState(
                collectionId,
                newId,
                wantedIds,
                DateTime.UtcNow);

            var librariesAfter = await _jellyfin.CountCollectionLibrariesAsync();

            if (librariesAfter > Math.Max(librariesBefore, 1))
            {
                result.Warning =
                    $"The collection was created, but Jellyfin now shows {librariesAfter} \"Collections\" libraries instead of one. " +
                    "Don't send any more collections until the extra one is removed in Jellyfin's Dashboard, under Libraries.";
            }

            return result;
        }

        // 3. Bring the existing Jellyfin collection in line.
        var currentIds =
            (await _jellyfin.GetCollectionItemIdsAsync(jellyfinCollection.Id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toAdd =
            wantedIds
                .Where(id => !currentIds.Contains(id))
                .ToList();

        var wantedSet =
            wantedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only what Simplifiarr put there, and only if it is no longer
        // wanted here. Anything added by hand in Jellyfin stays.
        var toRemove =
            previouslySynced
                .Where(id => currentIds.Contains(id) && !wantedSet.Contains(id))
                .ToList();

        if (toAdd.Count > 0)
        {
            await _jellyfin.AddToCollectionAsync(jellyfinCollection.Id, toAdd);
        }

        if (toRemove.Count > 0)
        {
            await _jellyfin.RemoveFromCollectionAsync(jellyfinCollection.Id, toRemove);
        }

        result.Added = toAdd.Count;
        result.Removed = toRemove.Count;
        result.AlreadyThere = wantedIds.Count - toAdd.Count;

        // Remember as "ours" what was ours before and is still wanted,
        // plus what was just added. Items that were already there by hand
        // are not claimed.
        var nowOurs =
            wantedIds
                .Where(id => previouslySynced.Contains(id) || toAdd.Contains(id))
                .ToList();

        _collectionService.SetJellyfinState(
            collectionId,
            jellyfinCollection.Id,
            nowOurs,
            DateTime.UtcNow);

        return result;
    }

    /// <summary>
    /// Sent one by one, a show's episodes fill a Jellyfin collection with
    /// dozens of loose episodes. So when the collection has every episode
    /// Jellyfin has of a show, the show itself is sent instead; failing
    /// that, any season it has all of is sent as the season. Only a
    /// hand-picked few episodes go as episodes.
    /// </summary>
    private static List<string> GroupEpisodes(
        List<string> wantedIds,
        Dictionary<string, HashSet<string>> episodesBySeries,
        Dictionary<string, HashSet<string>> episodesBySeason,
        Dictionary<string, HashSet<string>> seasonsBySeries)
    {
        var wanted = wantedIds.ToHashSet();

        // Episode id -> the show or season that stands in for it.
        var standIn = new Dictionary<string, string>();

        foreach (var (seriesId, episodes) in episodesBySeries)
        {
            if (!episodes.Overlaps(wanted))
            {
                continue;
            }

            if (episodes.IsSubsetOf(wanted))
            {
                foreach (var episode in episodes)
                {
                    standIn[episode] = seriesId;
                }

                continue;
            }

            foreach (var seasonId in seasonsBySeries.GetValueOrDefault(seriesId) ?? [])
            {
                var seasonEpisodes = episodesBySeason[seasonId];

                if (seasonEpisodes.IsSubsetOf(wanted))
                {
                    foreach (var episode in seasonEpisodes)
                    {
                        standIn[episode] = seasonId;
                    }
                }
            }
        }

        return wantedIds
            .Select(id => standIn.GetValueOrDefault(id, id))
            .Distinct()
            .ToList();
    }

    private static HashSet<string> Bucket(
        Dictionary<string, HashSet<string>> buckets,
        string key)
    {
        if (!buckets.TryGetValue(key, out var bucket))
        {
            buckets[key] = bucket = [];
        }

        return bucket;
    }

    public Task<int> RefreshMembersAsync(
        Guid collectionId)
    {
        var collection = _collectionService.Get(collectionId);

        if (collection is null)
        {
            return Task.FromResult(0);
        }

        var keys = new Dictionary<Guid, string>(collection.MemberKeys);
        var memberIds = collection.AssetIds.ToList();

        var lost = new List<Guid>();
        var changed = false;

        foreach (var assetId in memberIds)
        {
            var asset = _catalogService.GetAsset(assetId);

            if (asset is not null && File.Exists(asset.CanonicalPath))
            {
                var key = MemberKey(asset);

                if (!keys.TryGetValue(assetId, out var known) || known != key)
                {
                    keys[assetId] = key;
                    changed = true;
                }
            }
            else if (keys.ContainsKey(assetId))
            {
                lost.Add(assetId);
            }
        }

        var found = 0;

        if (lost.Count > 0)
        {
            var members = memberIds.ToHashSet();

            // Everything in the catalog that isn't already a member, by
            // what it is. Built only when something has actually gone
            // missing.
            var candidates =
                _catalogService.GetAssets()
                    .Where(asset => !members.Contains(asset.Id))
                    .GroupBy(MemberKey)
                    .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var lostId in lost)
            {
                var key = keys[lostId];

                if (!candidates.TryGetValue(key, out var matches))
                {
                    continue;
                }

                var onDisk =
                    matches
                        .Where(asset => File.Exists(asset.CanonicalPath))
                        .ToList();

                // Two copies of the same thing: no way to know which one
                // was meant, so leave it for the user.
                if (onDisk.Count != 1)
                {
                    continue;
                }

                var replacement = onDisk[0];

                memberIds[memberIds.IndexOf(lostId)] = replacement.Id;

                keys.Remove(lostId);
                keys[replacement.Id] = key;

                candidates.Remove(key);

                found++;
                changed = true;
            }
        }

        if (changed)
        {
            _collectionService.SetMembers(collectionId, memberIds, keys);
        }

        if (found > 0)
        {
            AppLog.Info(
                $"Collection \"{collection.Name}\": found {found} renamed or replaced item(s) again.");
        }

        return Task.FromResult(found);
    }

    /// <summary>
    /// What an item is, independent of where its file lives or what the
    /// file is called.
    /// </summary>
    private static string MemberKey(MediaAsset asset) =>
        string.Join(
            '|',
            asset.MediaType,
            asset.Title.Trim().ToLowerInvariant(),
            asset.Year,
            asset.Season,
            asset.Episode,
            asset.Author?.Trim().ToLowerInvariant(),
            asset.Album?.Trim().ToLowerInvariant(),
            asset.TrackNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public Task<int> GetOrphanedMemberCountAsync(
        Guid collectionId)
    {
        var collection = _collectionService.Get(collectionId);

        return Task.FromResult(
            collection?.AssetIds.Count(IsOrphaned) ?? 0);
    }

    public Task<List<MediaAsset>> GetOrphanedMembersAsync(
        Guid collectionId)
    {
        var collection = _collectionService.Get(collectionId);

        if (collection is null)
        {
            return Task.FromResult(new List<MediaAsset>());
        }

        return Task.FromResult(
            collection.AssetIds
                .Select(assetId => _catalogService.GetAsset(assetId))
                .Where(asset =>
                    asset is not null &&
                    !File.Exists(asset.CanonicalPath))
                .Select(asset => asset!)
                .ToList());
    }

    public Task<int> RemoveOrphanedMembersAsync(
        Guid collectionId)
    {
        var collection = _collectionService.Get(collectionId);

        if (collection is null)
        {
            return Task.FromResult(0);
        }

        var orphaned =
            collection.AssetIds
                .Where(IsOrphaned)
                .ToList();

        _collectionService.RemoveAssets(collectionId, orphaned);

        return Task.FromResult(orphaned.Count);
    }

    private bool IsOrphaned(
        Guid assetId)
    {
        var asset = _catalogService.GetAsset(assetId);

        return asset is null || !File.Exists(asset.CanonicalPath);
    }

    // Jellyfin and Windows can disagree on slash direction for the same
    // file; compare them the same way round.
    private static string NormalizePath(string path) =>
        path.Replace('/', '\\').TrimEnd('\\');

    private static string Describe(MediaAsset asset) =>
        asset.MediaType == MediaType.TvEpisode
            ? $"{asset.Title} S{asset.Season ?? 0:00}E{asset.Episode ?? 0:00}"
            : asset.Year.HasValue
                ? $"{asset.Title} ({asset.Year})"
                : asset.Title;
}
