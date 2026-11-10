namespace Simplifiarr.Core.Services;

/// <summary>
/// One thing Jellyfin knows about: a film, an episode, a track, a book -
/// or a collection.
/// </summary>
public sealed record JellyfinItem(
    string Id,
    string Name,
    string Type,
    string Path,
    string SeriesId = "",
    string SeasonId = "");

/// <summary>
/// The few Jellyfin calls Simplifiarr makes. Reads list what Jellyfin has;
/// the only things ever written are collections and what is in them -
/// never a library, a file or a user.
/// </summary>
public interface IJellyfinClient
{
    Task<bool> TestConnectionAsync();

    Task<bool> TestConnectionAsync(
        string url,
        string apiKey);

    /// <summary>
    /// Every film, episode, track, audiobook and book in Jellyfin's
    /// libraries that has a file path.
    /// </summary>
    Task<IReadOnlyList<JellyfinItem>> GetLibraryItemsAsync();

    /// <summary>
    /// The collections Jellyfin already has.
    /// </summary>
    Task<IReadOnlyList<JellyfinItem>> GetCollectionsAsync();

    /// <summary>
    /// The ids of the items inside one collection.
    /// </summary>
    Task<IReadOnlyList<string>> GetCollectionItemIdsAsync(
        string collectionId);

    /// <summary>
    /// How many "Collections" libraries Jellyfin has. It should be zero
    /// (none made yet) or one; more than one means something went wrong
    /// inside Jellyfin.
    /// </summary>
    Task<int> CountCollectionLibrariesAsync();

    /// <summary>
    /// Makes a new collection holding these items and returns its id.
    /// </summary>
    Task<string> CreateCollectionAsync(
        string name,
        IReadOnlyCollection<string> itemIds);

    Task AddToCollectionAsync(
        string collectionId,
        IReadOnlyCollection<string> itemIds);

    Task RemoveFromCollectionAsync(
        string collectionId,
        IReadOnlyCollection<string> itemIds);
}
