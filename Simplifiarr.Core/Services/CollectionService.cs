using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class CollectionService
    : ICollectionService
{
    private readonly object _sync = new();

    private readonly List<Collection> _collections = [];

    private readonly string _cachePath;

    public CollectionService()
    {
        _cachePath = AppPaths.DataFile("collections.json");

        LoadFromDisk();
    }

    public IReadOnlyList<Collection> GetAll()
    {
        lock (_sync)
        {
            return _collections
                .OrderBy(collection => collection.Name)
                .ToList();
        }
    }

    public Collection? Get(
        Guid collectionId)
    {
        lock (_sync)
        {
            return Find(collectionId);
        }
    }

    public Collection Create(
        string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();

        lock (_sync)
        {
            if (_collections.Any(existing =>
                    string.Equals(
                        existing.Name,
                        trimmed,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"There is already a collection called \"{trimmed}\".");
            }

            var collection =
                new Collection
                {
                    Name = trimmed
                };

            _collections.Add(collection);

            SaveToDisk();

            return collection;
        }
    }

    public void AddAsset(
        Guid collectionId,
        Guid assetId) =>
        AddAssets(collectionId, [assetId]);

    public void AddAssets(
        Guid collectionId,
        IEnumerable<Guid> assetIds)
    {
        lock (_sync)
        {
            var collection = Find(collectionId);

            if (collection is null)
            {
                return;
            }

            var existing = collection.AssetIds.ToHashSet();
            var added = false;

            foreach (var assetId in assetIds)
            {
                if (existing.Add(assetId))
                {
                    collection.AssetIds.Add(assetId);
                    added = true;
                }
            }

            if (added)
            {
                SaveToDisk();
            }
        }
    }

    public void RemoveAsset(
        Guid collectionId,
        Guid assetId) =>
        RemoveAssets(collectionId, [assetId]);

    public void RemoveAssets(
        Guid collectionId,
        IEnumerable<Guid> assetIds)
    {
        lock (_sync)
        {
            var collection = Find(collectionId);

            if (collection is null)
            {
                return;
            }

            var toRemove = assetIds.ToHashSet();

            if (collection.AssetIds.RemoveAll(toRemove.Contains) > 0)
            {
                foreach (var assetId in toRemove)
                {
                    collection.MemberKeys.Remove(assetId);
                }

                SaveToDisk();
            }
        }
    }

    public void SetMembers(
        Guid collectionId,
        IEnumerable<Guid> assetIds,
        IReadOnlyDictionary<Guid, string> memberKeys)
    {
        lock (_sync)
        {
            var collection = Find(collectionId);

            if (collection is null)
            {
                return;
            }

            collection.AssetIds = assetIds.Distinct().ToList();

            collection.MemberKeys =
                memberKeys
                    .Where(pair => collection.AssetIds.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);

            SaveToDisk();
        }
    }

    public void SetJellyfinState(
        Guid collectionId,
        string? jellyfinCollectionId,
        IEnumerable<string> syncedJellyfinItemIds,
        DateTime syncedUtc)
    {
        lock (_sync)
        {
            var collection = Find(collectionId);

            if (collection is null)
            {
                return;
            }

            collection.JellyfinCollectionId = jellyfinCollectionId;
            collection.SyncedJellyfinItemIds = syncedJellyfinItemIds.Distinct().ToList();
            collection.LastSyncedUtc = syncedUtc;

            SaveToDisk();
        }
    }

    public void Delete(
        Guid collectionId)
    {
        lock (_sync)
        {
            if (_collections.RemoveAll(collection =>
                    collection.Id == collectionId) > 0)
            {
                SaveToDisk();
            }
        }
    }

    private Collection? Find(Guid collectionId) =>
        _collections.FirstOrDefault(collection =>
            collection.Id == collectionId);

    private void LoadFromDisk()
    {
        lock (_sync)
        {
            var persisted =
                JsonFileStore.Load<List<Collection>>(_cachePath);

            if (persisted is null)
            {
                return;
            }

            _collections.Clear();
            _collections.AddRange(persisted);

            if (MergeSameNamedCollections())
            {
                SaveToDisk();
            }
        }
    }

    /// <summary>
    /// Before 2.0 a collection was a folder of hardlinks, so "Horror" had
    /// to exist once per drive. Now that a collection is just a list, those
    /// are one collection: fold every same-named group into its oldest
    /// member. Runs on every load but only ever changes old data.
    /// </summary>
    private bool MergeSameNamedCollections()
    {
        var merged = false;

        var groups =
            _collections
                .GroupBy(
                    collection => collection.Name.Trim(),
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .ToList();

        foreach (var group in groups)
        {
            var keep =
                group
                    .OrderBy(collection => collection.CreatedUtc)
                    .First();

            var seen = keep.AssetIds.ToHashSet();

            foreach (var other in group.Where(collection => !ReferenceEquals(collection, keep)))
            {
                foreach (var assetId in other.AssetIds)
                {
                    if (seen.Add(assetId))
                    {
                        keep.AssetIds.Add(assetId);
                    }
                }

                foreach (var pair in other.MemberKeys)
                {
                    keep.MemberKeys.TryAdd(pair.Key, pair.Value);
                }

                keep.JellyfinCollectionId ??= other.JellyfinCollectionId;

                _collections.Remove(other);
            }

            keep.DriveProfileName = string.Empty;

            AppLog.Info(
                $"Merged {group.Count()} per-drive collections called \"{keep.Name}\" into one ({keep.AssetIds.Count} items).");

            merged = true;
        }

        return merged;
    }

    private void SaveToDisk() =>
        JsonFileStore.Save(_cachePath, _collections);
}
