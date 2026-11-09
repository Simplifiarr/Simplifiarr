using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface ICollectionService
{
    IReadOnlyList<Collection> GetAll();

    Collection? Get(
        Guid collectionId);

    Collection Create(
        string name,
        string driveProfileName);

    void AddAsset(
        Guid collectionId,
        Guid assetId);

    void AddAssets(
        Guid collectionId,
        IEnumerable<Guid> assetIds);

    void RemoveAsset(
        Guid collectionId,
        Guid assetId);

    void SetLastBuiltUtc(
        Guid collectionId,
        DateTime builtUtc);

    void Delete(
        Guid collectionId);
}
