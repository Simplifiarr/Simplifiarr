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
/// A film, show or collection in Jellyfin, as far as choosing its poster
/// goes. The ids are the ones poster sites know the title by.
/// </summary>
public sealed record JellyfinPosterItem(
    string Id,
    string Name,
    string Type,
    int? Year,
    bool HasPoster,
    string TmdbId = "",
    string TvdbId = "",
    string ImdbId = "");

public sealed record JellyfinPosterPage(
    IReadOnlyList<JellyfinPosterItem> Items,
    int Total);

/// <summary>
/// A poster Jellyfin's own image search found for an item.
/// </summary>
public sealed record JellyfinRemoteImage(
    string Url,
    string ThumbnailUrl,
    string Provider,
    string Language,
    int? Width,
    int? Height,
    double? Rating);

public sealed record JellyfinImage(
    byte[] Bytes,
    string ContentType);

/// <summary>
/// The few Jellyfin calls Simplifiarr makes. Reads list what Jellyfin has;
/// the only things ever written are collections, what is in them, and an
/// item's poster - never a library, a file or a user.
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

    /// <summary>
    /// One page of Jellyfin's films ("Movie"), shows ("Series") or
    /// collections ("BoxSet"), by name, optionally narrowed by a search.
    /// </summary>
    Task<JellyfinPosterPage> FindPosterItemsAsync(
        string itemType,
        string? search,
        int start,
        int limit);

    /// <summary>Null when Jellyfin has no such item.</summary>
    Task<JellyfinPosterItem?> GetPosterItemAsync(
        string itemId);

    /// <summary>
    /// What is directly inside a collection.
    /// </summary>
    Task<IReadOnlyList<JellyfinPosterItem>> GetCollectionPosterItemsAsync(
        string collectionId);

    /// <summary>
    /// The posters Jellyfin's own image search (its metadata plugins) finds
    /// for an item.
    /// </summary>
    Task<IReadOnlyList<JellyfinRemoteImage>> GetRemotePostersAsync(
        string itemId);

    /// <summary>
    /// An item's current poster, or null when it has none.
    /// </summary>
    Task<JellyfinImage?> GetPosterAsync(
        string itemId,
        int? maxWidth = null);

    /// <summary>
    /// Replaces an item's poster with this image.
    /// </summary>
    Task SetPosterAsync(
        string itemId,
        byte[] image,
        string contentType);
}
