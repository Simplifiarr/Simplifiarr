namespace Simplifiarr.Core.Services;

/// <summary>
/// One picture that could be used for an item.
/// </summary>
public sealed record PosterOption(
    string Source,
    string Url,
    string ThumbnailUrl,
    string Language,
    int? Width,
    int? Height,
    double? Rating);

/// <summary>
/// The pictures found for an item, and a sentence for each source that
/// couldn't be asked.
/// </summary>
public sealed record PosterOptions(
    IReadOnlyList<PosterOption> Options,
    IReadOnlyList<string> Notes);

/// <summary>
/// What to do with a new backdrop when the item already has some.
/// </summary>
public enum BackdropChoice
{
    /// <summary>It becomes the first backdrop, in place of the old first one.</summary>
    ReplaceMain,

    /// <summary>It is added after the ones already there.</summary>
    Add
}

/// <summary>
/// One picture change, with what is needed to undo it.
/// </summary>
/// <param name="Previous">The picture it replaced; null if there was none.</param>
/// <param name="Added">A backdrop was added rather than replacing one.</param>
/// <param name="Backdrops">How many backdrops the item has now.</param>
public sealed record PictureChange(
    string ItemId,
    string PictureType,
    JellyfinImage? Previous,
    bool Added,
    int Backdrops);

/// <summary>
/// Finds pictures (posters, backdrops, logos...) for a Jellyfin item and
/// sets the one chosen. Sources: Jellyfin's own image search (whatever
/// metadata plugins it has), and fanart.tv when a personal API key is in
/// Settings. Any other picture can be used by giving its web address or
/// the file itself.
/// </summary>
public interface IPosterService
{
    Task<PosterOptions> GetOptionsAsync(
        JellyfinPosterItem item,
        string pictureType = "Primary");

    /// <summary>
    /// Fetches the picture at this address and sets it as the item's
    /// picture of this type.
    /// </summary>
    Task<PictureChange> SetFromUrlAsync(
        string itemId,
        string url,
        string pictureType = "Primary",
        BackdropChoice backdrop = BackdropChoice.ReplaceMain);

    /// <summary>Sets this picture as the item's picture of this type.</summary>
    Task<PictureChange> SetFromImageAsync(
        string itemId,
        byte[] image,
        string pictureType = "Primary",
        BackdropChoice backdrop = BackdropChoice.ReplaceMain);

    /// <summary>
    /// Gives an item another item's current picture of this type (e.g. a
    /// film's poster for the collection it is in).
    /// </summary>
    Task<PictureChange> SetFromItemAsync(
        string itemId,
        string sourceItemId,
        string pictureType = "Primary");

    /// <summary>Puts things back as they were before this change.</summary>
    Task UndoAsync(
        PictureChange change);
}
