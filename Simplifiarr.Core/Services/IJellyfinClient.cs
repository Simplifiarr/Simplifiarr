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
    string ImdbId = "")
{
    /// <summary>The kinds of picture it has ("Primary", "Backdrop", "Logo"...).</summary>
    public IReadOnlyList<string> Pictures { get; init; } = HasPoster ? ["Primary"] : [];

    /// <summary>How many backdrops it has.</summary>
    public int Backdrops { get; init; }

    /// <summary>For a season: its number, and the show it belongs to.</summary>
    public int? SeasonNumber { get; init; }

    public string SeriesId { get; init; } = string.Empty;

    public string SeriesTvdbId { get; init; } = string.Empty;

    public string SeriesTmdbId { get; init; } = string.Empty;

    public bool Has(string pictureType) =>
        Pictures.Contains(pictureType, StringComparer.OrdinalIgnoreCase);

    /// <summary>A copy that now has (or no longer has) this kind of picture.</summary>
    public JellyfinPosterItem With(string pictureType, bool has, int? backdrops = null)
    {
        var pictures =
            Pictures.Where(picture => !string.Equals(picture, pictureType, StringComparison.OrdinalIgnoreCase)).ToList();

        if (has)
        {
            pictures.Add(pictureType);
        }

        return this with
        {
            HasPoster = pictureType == "Primary" ? has : HasPoster,
            Pictures = pictures,
            Backdrops = backdrops ?? Backdrops
        };
    }
}

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
/// item's pictures (poster, backdrop, logo...) - never a library, a file
/// or a user.
/// </summary>
public interface IJellyfinClient
{
    Task<bool> TestConnectionAsync();

    Task<bool> TestConnectionAsync(
        string url,
        string apiKey);

    /// <summary>Every Jellyfin user: id and name.</summary>
    Task<IReadOnlyList<(string Id, string Name)>> GetUsersAsync();

    /// <summary>
    /// Each movie and show as one user sees it: when it was added and
    /// whether that user has watched it.
    /// </summary>
    Task<IReadOnlyList<JellyfinWatchItem>> GetWatchItemsAsync(string userId);

    /// <summary>The file paths of every episode this user has watched.</summary>
    Task<IReadOnlyList<string>> GetWatchedEpisodePathsAsync(string userId);

    /// <summary>    /// Every film, episode, track, audiobook and book in Jellyfin's
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
        string itemId,
        string imageType = "Primary");

    /// <summary>A show's seasons, each knowing its show's ids.</summary>
    Task<IReadOnlyList<JellyfinPosterItem>> GetSeasonsAsync(
        JellyfinPosterItem series);

    /// <summary>
    /// One of an item's pictures (its poster unless another type is
    /// given), or null when it has none.
    /// </summary>
    Task<JellyfinImage?> GetPosterAsync(
        string itemId,
        int? maxWidth = null,
        string imageType = "Primary",
        int index = 0);

    /// <summary>
    /// Replaces an item's picture of this type with this image. For a type
    /// it can have several of (backdrops), Jellyfin adds it to the end.
    /// </summary>
    Task SetPosterAsync(
        string itemId,
        byte[] image,
        string contentType,
        string imageType = "Primary");

    /// <summary>Removes one of an item's pictures.</summary>
    Task DeleteImageAsync(
        string itemId,
        string imageType,
        int index = 0);

    /// <summary>The files someone is playing right now, from Jellyfin's sessions.</summary>
    Task<IReadOnlyList<string>> GetPlayingPathsAsync();

    /// <summary>
    /// Tells Jellyfin these folders changed, so it looks at them again now
    /// rather than at its next library scan.
    /// </summary>
    Task FoldersChangedAsync(
        IEnumerable<string> folders);

    /// <summary>Swaps two of an item's pictures of one type (backdrops).</summary>
    Task SwapImagesAsync(
        string itemId,
        string imageType,
        int index,
        int otherIndex);
}

/// <summary>A movie or show as one Jellyfin user sees it.</summary>
public sealed record JellyfinWatchItem(
    string Id,
    bool IsSeries,
    string Path,
    DateTime? AddedUtc,
    bool Watched,
    bool Started,
    DateTime? LastWatchedUtc,
    bool Favorite = false);