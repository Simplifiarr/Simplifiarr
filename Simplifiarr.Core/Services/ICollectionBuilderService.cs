using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Looks after a collection's members: which ones still exist, and sending
/// the collection to Jellyfin.
/// </summary>
public interface ICollectionBuilderService
{
    /// <summary>
    /// Makes Jellyfin's collection of the same name match this one:
    /// creates it if Jellyfin has none, adds members it lacks, and takes
    /// out members Simplifiarr added earlier that have since been removed
    /// here. Anything added to the collection inside Jellyfin is left
    /// alone. No file is touched.
    /// </summary>
    Task<CollectionSyncResult> SyncToJellyfinAsync(
        Guid collectionId);

    /// <summary>
    /// Keeps a collection's members attached to the right items. A member
    /// is remembered by what it is (type, title, year, season, episode...)
    /// as well as by its catalog id, so when a file is renamed or replaced
    /// by a better copy - which gives it a new id - the member is pointed
    /// at the new file instead of being lost. Only done when exactly one
    /// item matches. Returns how many members were found again.
    /// </summary>
    Task<int> RefreshMembersAsync(
        Guid collectionId);

    /// <summary>
    /// Number of members whose library file no longer exists - either the
    /// catalog no longer has the item at all, or it does but the file is
    /// gone. Cheap, safe to call once per collection in a list.
    /// </summary>
    Task<int> GetOrphanedMemberCountAsync(
        Guid collectionId);

    /// <summary>
    /// The orphaned members that can still be named (for display). Members
    /// whose item has left the catalog entirely aren't included here, but
    /// still count toward GetOrphanedMemberCountAsync's total.
    /// </summary>
    Task<List<MediaAsset>> GetOrphanedMembersAsync(
        Guid collectionId);

    /// <summary>
    /// Takes every orphaned member out of the collection. Returns how many.
    /// </summary>
    Task<int> RemoveOrphanedMembersAsync(
        Guid collectionId);
}
