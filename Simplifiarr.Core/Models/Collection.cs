namespace Simplifiarr.Core.Models;

/// <summary>
/// A named group of library items the user has put together - "Horror",
/// "Christmas", "Family favourites". A collection is just a list: it holds no
/// files of its own, so it can include items from any drive. Sending it to
/// Jellyfin makes (or updates) a collection of the same name there.
/// </summary>
public class Collection
{
    public Guid Id { get; set; } =
        Guid.NewGuid();

    public string Name { get; set; } =
        string.Empty;

    /// <summary>
    /// The catalog items in this collection.
    /// </summary>
    public List<Guid> AssetIds { get; set; } =
        [];

    /// <summary>
    /// What each member is - "movie, this title, this year" - so that when
    /// its file is renamed or replaced by a better copy (which gives it a
    /// new catalog id) the member can be found again instead of dropping
    /// out of the collection.
    /// </summary>
    public Dictionary<Guid, string> MemberKeys { get; set; } =
        [];

    public DateTime CreatedUtc { get; set; } =
        DateTime.UtcNow;

    /// <summary>
    /// The matching collection in Jellyfin, once it has been sent there.
    /// </summary>
    public string? JellyfinCollectionId { get; set; }

    public DateTime? LastSyncedUtc { get; set; }

    /// <summary>
    /// The Jellyfin items Simplifiarr itself put in the Jellyfin
    /// collection. Only these are ever taken back out - anything someone
    /// added to the collection inside Jellyfin is left alone.
    /// </summary>
    public List<string> SyncedJellyfinItemIds { get; set; } =
        [];

    /// <summary>
    /// From before 2.0, when a collection was a folder of hardlinks and so
    /// had to live on one drive. No longer used; kept so old files load,
    /// and so same-named per-drive collections can be merged into one.
    /// </summary>
    public string DriveProfileName { get; set; } =
        string.Empty;

    /// <summary>From before 2.0. No longer used.</summary>
    public DateTime? LastBuiltUtc { get; set; }
}

/// <summary>
/// What happened when a collection was sent to Jellyfin.
/// </summary>
public class CollectionSyncResult
{
    /// <summary>
    /// Set when nothing could be done at all; the other fields are then empty.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Something the user should look at even though the sync went through.
    /// </summary>
    public string? Warning { get; set; }

    public bool CreatedInJellyfin { get; set; }

    public int Added { get; set; }

    public int AlreadyThere { get; set; }

    public int Removed { get; set; }

    /// <summary>
    /// Members Jellyfin doesn't have in any of its libraries (or hasn't
    /// scanned yet), by title.
    /// </summary>
    public List<string> NotInJellyfin { get; set; } =
        [];

    /// <summary>
    /// Members whose file is gone from disk.
    /// </summary>
    public int MissingFiles { get; set; }

    public bool Succeeded =>
        Error is null;
}
