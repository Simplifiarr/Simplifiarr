using System.Diagnostics;
using System.Text.Json;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class CollectionService
    : ICollectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _sync = new();

    private readonly List<Collection> _collections = [];

    private readonly string _cachePath;

    public CollectionService()
    {
        _cachePath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "Simplifiarr",
            "PromotionCache",
            "collections.json");

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
            try
            {
                if (!File.Exists(_cachePath))
                {
                    return;
                }

                var json =
                    File.ReadAllText(_cachePath);

                var persisted =
                    JsonSerializer.Deserialize<List<Collection>>(
                        json,
                        JsonOptions);

                if (persisted is null)
                {
                    return;
                }

                _collections.Clear();
                _collections.AddRange(persisted);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Unable to load collections: {exception}");
            }
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var directory =
                Path.GetDirectoryName(_cachePath)!;

            Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(
                _collections,
                JsonOptions);

            var temporaryPath = _cachePath + ".tmp";

            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _cachePath, true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Unable to save collections: {exception}");
        }
    }
}
