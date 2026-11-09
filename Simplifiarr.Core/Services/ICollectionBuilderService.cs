using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface ICollectionBuilderService
{
    /// <summary>
    /// Jellyfin sync is temporarily disabled (see CollectionBuilderService)
    /// - this always returns a failure result explaining why, without
    /// contacting Jellyfin at all. Collection membership is unaffected.
    /// </summary>
    Task<CollectionBuildResult> BuildAsync(
        Guid collectionId);

    /// <summary>
    /// No-op while Jellyfin sync is disabled - nothing is ever built, so
    /// there is nothing to delete. Clears any stale JellyfinCollectionId
    /// left over from before sync was disabled.
    /// </summary>
    Task DeleteBuiltFilesAsync(
        Guid collectionId);

    /// <summary>
    /// Total number of members whose original library file no longer
    /// exists - either the catalog no longer has the asset at all, or the
    /// asset is still cataloged but its file is gone. Cheap (File.Exists
    /// checks only), safe to call once per collection when rendering a
    /// list of many collections.
    /// </summary>
    Task<int> GetOrphanedMemberCountAsync(
        Guid collectionId);

    /// <summary>
    /// The orphaned members that can still be resolved to a MediaAsset (for
    /// display). Members whose asset has been fully pruned from the
    /// catalog aren't included here, but still count toward
    /// GetOrphanedMemberCountAsync's total.
    /// </summary>
    Task<List<MediaAsset>> GetOrphanedMembersAsync(
        Guid collectionId);

    /// <summary>
    /// Removes each orphaned member from this collection's membership.
    /// Never touches the original library file - there isn't one anymore,
    /// that's the whole point. Returns the count removed.
    /// </summary>
    Task<int> RemoveOrphanedMembersAsync(
        Guid collectionId);
}
