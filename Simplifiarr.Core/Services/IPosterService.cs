namespace Simplifiarr.Core.Services;

/// <summary>
/// One poster that could be used for an item.
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
/// The posters found for an item, and a sentence for each source that
/// couldn't be asked.
/// </summary>
public sealed record PosterOptions(
    IReadOnlyList<PosterOption> Options,
    IReadOnlyList<string> Notes);

/// <summary>
/// Finds posters for a Jellyfin item and sets the one chosen. Sources:
/// Jellyfin's own image search (whatever metadata plugins it has), and
/// fanart.tv when a personal API key is in Settings. Any other picture can
/// be used by giving its web address or the file itself.
/// </summary>
public interface IPosterService
{
    Task<PosterOptions> GetOptionsAsync(
        JellyfinPosterItem item);

    /// <summary>
    /// Fetches the picture at this address and makes it the item's poster.
    /// Returns the poster it replaced (null if there was none), so the
    /// change can be undone.
    /// </summary>
    Task<JellyfinImage?> SetFromUrlAsync(
        string itemId,
        string url);

    /// <summary>
    /// Makes this picture the item's poster. Returns the poster it replaced.
    /// </summary>
    Task<JellyfinImage?> SetFromImageAsync(
        string itemId,
        byte[] image);

    /// <summary>
    /// Makes one item's current poster another item's poster (e.g. a film's
    /// poster for the collection it is in). Returns the poster it replaced.
    /// </summary>
    Task<JellyfinImage?> SetFromItemAsync(
        string itemId,
        string sourceItemId);
}
