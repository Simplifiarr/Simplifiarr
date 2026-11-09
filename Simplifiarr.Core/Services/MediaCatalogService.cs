using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class MediaCatalogService
    : IMediaCatalogService
{
    private readonly object _sync = new();

    private readonly List<MediaAsset> _assets =
        [];

    // Lookups by path and by id happen once per file on every scan, so
    // they go through these indexes instead of walking the whole list.
    // An asset's path and id never change after it is added.
    private readonly Dictionary<string, MediaAsset> _assetsByPath =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<Guid, MediaAsset> _assetsById =
        [];

    private readonly List<SourceRecord> _sources =
        [];

    private readonly List<TorrentRecord> _torrents =
        [];

    private readonly string _cachePath;

    public MediaCatalogService()
    {
        _cachePath = AppPaths.DataFile("media-catalog.json");

        LoadFromDisk();
    }

    public IReadOnlyCollection<MediaAsset> GetAssets()
    {
        lock (_sync)
        {
            return _assets.ToList().AsReadOnly();
        }
    }

    public MediaAsset? GetAsset(
        Guid assetId)
    {
        lock (_sync)
        {
            return _assetsById.GetValueOrDefault(assetId);
        }
    }

    public MediaAsset? FindAssetByTitle(
        string normalizedTitle)
    {
        var normalizedKey =
            TitleNormalizer.NormalizeForMatch(normalizedTitle);

        lock (_sync)
        {
            return _assets.FirstOrDefault(asset =>
                TitleNormalizer.NormalizeForMatch(asset.Title) == normalizedKey);
        }
    }

    public MediaAsset? FindAssetByTitleAndYear(
        string normalizedTitle,
        int? year)
    {
        if (year is null)
        {
            return FindAssetByTitle(
                normalizedTitle);
        }

        var normalizedKey =
            TitleNormalizer.NormalizeForMatch(normalizedTitle);

        lock (_sync)
        {
            return _assets.FirstOrDefault(asset =>
                TitleNormalizer.NormalizeForMatch(asset.Title) == normalizedKey
                &&
                asset.Year == year);
        }
    }

    public MediaAsset? FindTvEpisodeAsset(
        string normalizedTitle,
        int? season,
        int? episode)
    {
        var normalizedKey =
            TitleNormalizer.NormalizeForMatch(normalizedTitle);

        lock (_sync)
        {
            return _assets.FirstOrDefault(asset =>
                TitleNormalizer.NormalizeForMatch(asset.Title) == normalizedKey
                &&
                asset.Season == season
                &&
                asset.Episode == episode);
        }
    }

    public MediaAsset? FindBookAsset(
        string title,
        string? author,
        string format)
    {
        lock (_sync)
        {
            return _assets.FirstOrDefault(asset =>
                string.Equals(
                    asset.Title,
                    title,
                    StringComparison.OrdinalIgnoreCase)
                &&
                string.Equals(
                    asset.Author ?? string.Empty,
                    author ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                &&
                string.Equals(
                    Path.GetExtension(asset.CanonicalPath).TrimStart('.'),
                    format.TrimStart('.'),
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    public MediaAsset? FindAudiobookAsset(
        string title,
        string? author,
        string format)
    {
        lock (_sync)
        {
            return _assets.FirstOrDefault(asset =>
                asset.MediaType == MediaType.Audiobook
                &&
                string.Equals(
                    asset.Title,
                    title,
                    StringComparison.OrdinalIgnoreCase)
                &&
                string.Equals(
                    asset.Author ?? string.Empty,
                    author ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                &&
                string.Equals(
                    Path.GetExtension(asset.CanonicalPath).TrimStart('.'),
                    format.TrimStart('.'),
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    public MediaAsset? FindMusicAsset(
        string title,
        string? artist,
        string? album,
        string format)
    {
        lock (_sync)
        {
            return _assets.FirstOrDefault(asset =>
                asset.MediaType == MediaType.Music
                &&
                string.Equals(
                    asset.Title,
                    title,
                    StringComparison.OrdinalIgnoreCase)
                &&
                string.Equals(
                    asset.Author ?? string.Empty,
                    artist ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                &&
                string.Equals(
                    asset.Album ?? string.Empty,
                    album ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                &&
                string.Equals(
                    Path.GetExtension(asset.CanonicalPath).TrimStart('.'),
                    format.TrimStart('.'),
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    public MediaAsset? FindComicAsset(
        string title,
        string? series,
        double? issue,
        string format)
    {
        lock (_sync)
        {
            return _assets.FirstOrDefault(asset =>
                asset.MediaType == MediaType.Comic
                &&
                string.Equals(
                    asset.Title,
                    title,
                    StringComparison.OrdinalIgnoreCase)
                &&
                string.Equals(
                    asset.Album ?? string.Empty,
                    series ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase)
                &&
                asset.TrackNumber == issue
                &&
                string.Equals(
                    Path.GetExtension(asset.CanonicalPath).TrimStart('.'),
                    format.TrimStart('.'),
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    public MediaAsset? FindAssetByPath(
        string canonicalPath)
    {
        if (string.IsNullOrWhiteSpace(canonicalPath))
        {
            return null;
        }

        var normalizedPath =
            Path.GetFullPath(canonicalPath);

        lock (_sync)
        {
            return _assetsByPath.GetValueOrDefault(normalizedPath);
        }
    }

    public void AddAsset(
        MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        lock (_sync)
        {
            _assets.Add(asset);

            IndexAsset(asset);
        }
    }

    // When two assets share a path or id, the earlier one wins - the same
    // answer the old "first match in the list" search gave.
    private void IndexAsset(
        MediaAsset asset)
    {
        _assetsById.TryAdd(asset.Id, asset);

        if (!string.IsNullOrWhiteSpace(asset.CanonicalPath))
        {
            _assetsByPath.TryAdd(
                Path.GetFullPath(asset.CanonicalPath),
                asset);
        }
    }

    private void RebuildIndexes()
    {
        _assetsByPath.Clear();
        _assetsById.Clear();

        foreach (var asset in _assets)
        {
            IndexAsset(asset);
        }
    }

    public void RemoveAssetsUnderPath(
        string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        var normalizedRoot =
            Path.GetFullPath(rootPath)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

        lock (_sync)
        {
            var idsToRemove =
                _assets
                    .Where(asset =>
                        IsUnderRoot(asset.CanonicalPath, normalizedRoot))
                    .Select(asset => asset.Id)
                    .ToHashSet();

            if (idsToRemove.Count == 0)
            {
                return;
            }

            _assets.RemoveAll(asset => idsToRemove.Contains(asset.Id));
            _sources.RemoveAll(source => idsToRemove.Contains(source.MediaAssetId));

            RebuildIndexes();

            SaveToDiskCore();
        }
    }

    public void RemoveAssetsUnderPathExcept(
        string rootPath,
        IReadOnlyCollection<string> keepPaths)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return;
        }

        var normalizedRoot =
            Path.GetFullPath(rootPath)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

        var keepSet =
            keepPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            var idsToRemove =
                _assets
                    .Where(asset =>
                        IsUnderRoot(asset.CanonicalPath, normalizedRoot) &&
                        !keepSet.Contains(Path.GetFullPath(asset.CanonicalPath)))
                    .Select(asset => asset.Id)
                    .ToHashSet();

            if (idsToRemove.Count == 0)
            {
                return;
            }

            _assets.RemoveAll(asset => idsToRemove.Contains(asset.Id));
            _sources.RemoveAll(source => idsToRemove.Contains(source.MediaAssetId));

            RebuildIndexes();

            SaveToDiskCore();
        }
    }

    private static bool IsUnderRoot(
        string path,
        string normalizedRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalizedPath =
            Path.GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

        return normalizedPath.Equals(
                   normalizedRoot,
                   StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<SourceRecord> GetSources()
    {
        lock (_sync)
        {
            return _sources.ToList().AsReadOnly();
        }
    }

    public IReadOnlyCollection<SourceRecord> GetSourcesForAsset(
        Guid assetId)
    {
        lock (_sync)
        {
            return _sources
                .Where(source =>
                    source.MediaAssetId == assetId)
                .ToList()
                .AsReadOnly();
        }
    }

    public SourceRecord? GetFilesystemSource(
        Guid assetId)
    {
        lock (_sync)
        {
            return _sources.FirstOrDefault(source =>
                source.MediaAssetId == assetId &&
                source.SourceType == SourceType.Filesystem &&
                !string.IsNullOrWhiteSpace(source.ExternalId));
        }
    }

    public bool HasSourceType(
        Guid assetId,
        SourceType sourceType)
    {
        lock (_sync)
        {
            return _sources.Any(source =>
                source.MediaAssetId == assetId &&
                source.SourceType == sourceType);
        }
    }

    public void AddSource(
        SourceRecord source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_sync)
        {
            _sources.Add(source);
        }
    }

    public IReadOnlyCollection<TorrentRecord> GetTorrents()
    {
        lock (_sync)
        {
            return _torrents.ToList().AsReadOnly();
        }
    }

    public void AddTorrent(
        TorrentRecord torrent)
    {
        ArgumentNullException.ThrowIfNull(torrent);

        lock (_sync)
        {
            _torrents.Add(torrent);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _assets.Clear();
            _sources.Clear();
            _torrents.Clear();

            RebuildIndexes();

            SaveToDiskCore();
        }
    }

    public void SaveToDisk()
    {
        lock (_sync)
        {
            SaveToDiskCore();
        }
    }

    private void LoadFromDisk()
    {
        lock (_sync)
        {
            var snapshot =
                JsonFileStore.Load<CatalogSnapshot>(_cachePath);

            if (snapshot is null)
            {
                return;
            }

            _assets.Clear();
            _assets.AddRange(snapshot.Assets);

            _sources.Clear();
            _sources.AddRange(snapshot.Sources);

            var removedCount =
                _assets.RemoveAll(asset =>
                    string.IsNullOrWhiteSpace(asset.CanonicalPath) ||
                    !File.Exists(asset.CanonicalPath));

            var remainingAssetIds =
                _assets
                    .Select(asset => asset.Id)
                    .ToHashSet();

            var removedSourceCount =
                _sources.RemoveAll(source =>
                    !remainingAssetIds.Contains(source.MediaAssetId));

            RebuildIndexes();

            if (removedCount > 0 ||
                removedSourceCount > 0)
            {
                SaveToDiskCore();
            }
        }
    }

    private void SaveToDiskCore() =>
        JsonFileStore.Save(
            _cachePath,
            new CatalogSnapshot
            {
                Assets = _assets,
                Sources = _sources
            });

    private sealed class CatalogSnapshot
    {
        public List<MediaAsset> Assets { get; set; } =
            [];

        public List<SourceRecord> Sources { get; set; } =
            [];
    }
}
