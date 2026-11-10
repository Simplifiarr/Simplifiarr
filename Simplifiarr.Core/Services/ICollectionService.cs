using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface ICollectionService
{
    IReadOnlyList<Collection> GetAll();

    Collection? Get(
        Guid collectionId);

    /// <summary>
    /// Makes a new, empty collection. Throws if one with that name already
    /// exists - Jellyfin identifies a collection by its name.
    /// </summary>
    Collection Create(
        string name);

    void AddAsset(
        Guid collectionId,
        Guid assetId);

    void AddAssets(
        Guid collectionId,
        IEnumerable<Guid> assetIds);

    void RemoveAsset(
        Guid collectionId,
        Guid assetId);

    void RemoveAssets(
        Guid collectionId,
        IEnumerable<Guid> assetIds);

    /// <summary>
    /// Replaces a collection's member list and what each member is, after
    /// members whose file was renamed or replaced have been found again.
    /// </summary>
    void SetMembers(
        Guid collectionId,
        IEnumerable<Guid> assetIds,
        IReadOnlyDictionary<Guid, string> memberKeys);

    /// <summary>
    /// Records the result of sending a collection to Jellyfin.
    /// </summary>
    void SetJellyfinState(
        Guid collectionId,
        string? jellyfinCollectionId,
        IEnumerable<string> syncedJellyfinItemIds,
        DateTime syncedUtc);

    void Delete(
        Guid collectionId);
}
