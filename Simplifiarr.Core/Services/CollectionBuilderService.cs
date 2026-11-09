using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Jellyfin sync is temporarily disabled - both ways of creating a native
/// Jellyfin collection (its REST API, and hand-writing a collection.xml
/// under its data/collections folder) were confirmed broken/unreliable
/// against a real Jellyfin 12.0.0 install during testing, including a
/// real incident where the broken API repeatedly spawned duplicate
/// internal library folders. BuildAsync/DeleteBuiltFilesAsync are stubbed
/// out until a working approach is found - collection membership
/// (Collection.AssetIds) and orphan detection still work normally.
/// </summary>
public class CollectionBuilderService
    : ICollectionBuilderService
{
    private readonly ICollectionService _collectionService;
    private readonly IMediaCatalogService _catalogService;

    public CollectionBuilderService(
        ICollectionService collectionService,
        IMediaCatalogService catalogService)
    {
        _collectionService = collectionService;
        _catalogService = catalogService;
    }

    public Task<CollectionBuildResult> BuildAsync(
        Guid collectionId)
    {
        var collection =
            _collectionService.Get(collectionId);

        var result =
            new CollectionBuildResult
            {
                Requested = collection?.AssetIds.Count ?? 0
            };

        result.FailureMessages.Add(
            "Jellyfin collection sync is temporarily disabled - both the Jellyfin API and the collection.xml filesystem approach were confirmed unreliable against a real Jellyfin 12.0.0 install. Membership below is still tracked normally; nothing is synced to Jellyfin right now.");

        return Task.FromResult(result);
    }

    public Task DeleteBuiltFilesAsync(
        Guid collectionId)
    {
        // Nothing is ever built right now, so nothing to delete.
        return Task.CompletedTask;
    }

    public async Task<int> GetOrphanedMemberCountAsync(
        Guid collectionId)
    {
        var collection =
            _collectionService.Get(collectionId);

        if (collection is null)
        {
            return 0;
        }

        return collection.AssetIds.Count(assetId => IsOrphaned(assetId));
    }

    public Task<List<MediaAsset>> GetOrphanedMembersAsync(
        Guid collectionId)
    {
        var collection =
            _collectionService.Get(collectionId);

        if (collection is null)
        {
            return Task.FromResult(new List<MediaAsset>());
        }

        var orphanedMembers =
            collection.AssetIds
                .Select(assetId => _catalogService.GetAsset(assetId))
                .Where(asset =>
                    asset is not null &&
                    !File.Exists(asset.CanonicalPath))
                .Select(asset => asset!)
                .ToList();

        return Task.FromResult(orphanedMembers);
    }

    public Task<int> RemoveOrphanedMembersAsync(
        Guid collectionId)
    {
        var collection =
            _collectionService.Get(collectionId);

        if (collection is null)
        {
            return Task.FromResult(0);
        }

        var removedCount = 0;

        foreach (var assetId in collection.AssetIds.ToList())
        {
            if (!IsOrphaned(assetId))
            {
                continue;
            }

            _collectionService.RemoveAsset(collectionId, assetId);

            removedCount++;
        }

        return Task.FromResult(removedCount);
    }

    private bool IsOrphaned(
        Guid assetId)
    {
        var asset =
            _catalogService.GetAsset(assetId);

        return asset is null || !File.Exists(asset.CanonicalPath);
    }
}
