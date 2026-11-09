using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IMediaCatalogService
{
    IReadOnlyCollection<MediaAsset> GetAssets();

    MediaAsset? GetAsset(
        Guid assetId);

    MediaAsset? FindAssetByTitle(
        string normalizedTitle);

    MediaAsset? FindAssetByTitleAndYear(
        string normalizedTitle,
        int? year);

    MediaAsset? FindTvEpisodeAsset(
        string normalizedTitle,
        int? season,
        int? episode);

    MediaAsset? FindBookAsset(
        string title,
        string? author,
        string format);

    MediaAsset? FindAudiobookAsset(
        string title,
        string? author,
        string format);

    MediaAsset? FindMusicAsset(
        string title,
        string? artist,
        string? album,
        string format);

    MediaAsset? FindComicAsset(
        string title,
        string? series,
        double? issue,
        string format);

    MediaAsset? FindAssetByPath(
        string canonicalPath);

    void AddAsset(
        MediaAsset asset);

    void RemoveAssetsUnderPath(
        string rootPath);

    /// <summary>
    /// Removes catalog assets under rootPath whose CanonicalPath is not in
    /// keepPaths - used to prune entries for files genuinely deleted from
    /// disk during a rescan, without discarding the Id/enrichment data
    /// (Genres, TmdbId/TvdbId, Collection membership) of files that are
    /// still present. See MediaCatalogPopulationService.PopulateAsync.
    /// </summary>
    void RemoveAssetsUnderPathExcept(
        string rootPath,
        IReadOnlyCollection<string> keepPaths);

    IReadOnlyCollection<SourceRecord> GetSources();

    IReadOnlyCollection<SourceRecord> GetSourcesForAsset(
        Guid assetId);

    SourceRecord? GetFilesystemSource(
        Guid assetId);

    bool HasSourceType(
        Guid assetId,
        SourceType sourceType);

    void AddSource(
        SourceRecord source);

    IReadOnlyCollection<TorrentRecord> GetTorrents();

    void AddTorrent(
        TorrentRecord torrent);

    void Clear();

    /// <summary>
    /// Persists the current assets/sources to disk. Add/RemoveAssetsUnderPath
    /// don't save on every call (they run in tight per-file loops during a
    /// scan) - callers that mutate assets in bulk, or mutate asset fields
    /// directly (e.g. Arr enrichment), must call this once when done.
    /// </summary>
    void SaveToDisk();
}
