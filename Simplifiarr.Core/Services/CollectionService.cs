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
            return _collections.FirstOrDefault(collection =>
                collection.Id == collectionId);
        }
    }

    public Collection Create(
        string name,
        string driveProfileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(driveProfileName);

        lock (_sync)
        {
            var collection =
                new Collection
                {
                    Name = name,
                    DriveProfileName = driveProfileName
                };

            _collections.Add(collection);

            SaveToDisk();

            return collection;
        }
    }

    public void AddAsset(
        Guid collectionId,
        Guid assetId)
    {
        lock (_sync)
        {
            var collection =
                _collections.FirstOrDefault(collection =>
                    collection.Id == collectionId);

            if (collection is null ||
                collection.AssetIds.Contains(assetId))
            {
                return;
            }

            collection.AssetIds.Add(assetId);

            SaveToDisk();
        }
    }

    public void AddAssets(
        Guid collectionId,
        IEnumerable<Guid> assetIds)
    {
        lock (_sync)
        {
            var collection =
                _collections.FirstOrDefault(collection =>
                    collection.Id == collectionId);

            if (collection is null)
            {
                return;
            }

            var added = false;

            foreach (var assetId in assetIds)
            {
                if (collection.AssetIds.Contains(assetId))
                {
                    continue;
                }

                collection.AssetIds.Add(assetId);
                added = true;
            }

            if (added)
            {
                SaveToDisk();
            }
        }
    }

    public void RemoveAsset(
        Guid collectionId,
        Guid assetId)
    {
        lock (_sync)
        {
            var collection =
                _collections.FirstOrDefault(collection =>
                    collection.Id == collectionId);

            if (collection is null)
            {
                return;
            }

            if (collection.AssetIds.Remove(assetId))
            {
                SaveToDisk();
            }
        }
    }

    public void SetLastBuiltUtc(
        Guid collectionId,
        DateTime builtUtc)
    {
        lock (_sync)
        {
            var collection =
                _collections.FirstOrDefault(collection =>
                    collection.Id == collectionId);

            if (collection is null)
            {
                return;
            }

            collection.LastBuiltUtc = builtUtc;

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
        }
    }

    private void SaveToDisk() =>
        JsonFileStore.Save(_cachePath, _collections);
}
