using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class MediaCatalogPopulationService
    : IMediaCatalogPopulationService
{
    private readonly IMediaCatalogService _catalogService;

    private readonly IReleaseParser _releaseParser;

    private readonly IMediaMetadataExtractor _metadataExtractor;

    private readonly IBookMetadataExtractor _bookMetadataExtractor;

    private readonly IAudiobookMetadataExtractor _audiobookMetadataExtractor;

    private readonly IMusicMetadataExtractor _musicMetadataExtractor;

    private readonly IComicMetadataExtractor _comicMetadataExtractor;

    public MediaCatalogPopulationService(
        IMediaCatalogService catalogService,
        IReleaseParser releaseParser,
        IMediaMetadataExtractor metadataExtractor,
        IBookMetadataExtractor bookMetadataExtractor,
        IAudiobookMetadataExtractor audiobookMetadataExtractor,
        IMusicMetadataExtractor musicMetadataExtractor,
        IComicMetadataExtractor comicMetadataExtractor)
    {
        _catalogService = catalogService;
        _releaseParser = releaseParser;
        _metadataExtractor = metadataExtractor;
        _bookMetadataExtractor = bookMetadataExtractor;
        _audiobookMetadataExtractor = audiobookMetadataExtractor;
        _musicMetadataExtractor = musicMetadataExtractor;
        _comicMetadataExtractor = comicMetadataExtractor;
    }

    public async Task<int> PopulateAsync(
        IEnumerable<FileRecord> files,
        MediaType? mediaTypeOverride = null)
    {
        var count = 0;

        foreach (var file in files)
        {
            if (file.FileType is
                FileType.Subtitle or
                FileType.Metadata or
                FileType.Unknown)
            {
                continue;
            }

            if (string.Equals(
                    file.FileName,
                    "theme.mp3",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existingAsset =
                _catalogService.FindAssetByPath(file.FullPath);

            if (existingAsset is not null)
            {
                await RefreshStructuralFieldsAsync(existingAsset, file);

                count++;

                continue;
            }

            MediaAsset asset;

            if (file.FileType == FileType.Book)
            {
                var bookMetadata =
                    _bookMetadataExtractor.Extract(
                        file.FullPath);

                asset = new MediaAsset
                {
                    Title = bookMetadata.Title,
                    Author = bookMetadata.Author,
                    CanonicalPath = file.FullPath,
                    FileSizeBytes = file.SizeBytes,
                    MediaType = MediaType.Book
                };
            }
            else if (file.FileType == FileType.Audio &&
                     mediaTypeOverride == MediaType.Music)
            {
                var musicMetadata =
                    _musicMetadataExtractor.Extract(
                        file.FullPath);

                asset = new MediaAsset
                {
                    Title = musicMetadata.Title,
                    Author = musicMetadata.Artist,
                    Album = musicMetadata.Album,
                    TrackNumber = musicMetadata.TrackNumber,
                    CanonicalPath = file.FullPath,
                    FileSizeBytes = file.SizeBytes,
                    MediaType = MediaType.Music
                };
            }
            else if (file.FileType == FileType.Audio)
            {
                var audiobookMetadata =
                    _audiobookMetadataExtractor.Extract(
                        file.FullPath);

                asset = new MediaAsset
                {
                    Title = audiobookMetadata.Title,
                    Author = audiobookMetadata.Author,
                    CanonicalPath = file.FullPath,
                    FileSizeBytes = file.SizeBytes,
                    MediaType = MediaType.Audiobook
                };
            }
            else if (file.FileType == FileType.Comic)
            {
                var comicMetadata =
                    _comicMetadataExtractor.Extract(
                        file.FullPath);

                asset = new MediaAsset
                {
                    Title = comicMetadata.Title,
                    Author = comicMetadata.Author,
                    Album = comicMetadata.Series,
                    TrackNumber = comicMetadata.Issue,
                    Year = comicMetadata.Year,
                    CanonicalPath = file.FullPath,
                    FileSizeBytes = file.SizeBytes,
                    MediaType = MediaType.Comic
                };
            }
            else
            {
                var parsed =
                    _releaseParser.Parse(
                        Path.GetFileNameWithoutExtension(
                            file.FileName));

                var metadata =
                    await _metadataExtractor.ExtractAsync(
                        file.FullPath);

                asset = new MediaAsset
                {
                    Title = parsed.NormalizedTitle,
                    Year = parsed.Year,
                    Season = parsed.Season,
                    Episode = parsed.Episode,

                    Resolution =
                        metadata?.Resolution ??
                        parsed.Resolution,

                    Width =
                        metadata?.Width ?? 0,

                    Height =
                        metadata?.Height ?? 0,

                    VideoCodec =
                        metadata?.Codec ??
                        string.Empty,

                    CanonicalPath = file.FullPath,

                    FileSizeBytes = file.SizeBytes,

                    // FileType.Audio is handled by the Music/Audiobook
                    // branches above, before this switch runs.
                    MediaType = file.FileType switch
                    {
                        FileType.Video when parsed.IsTv => MediaType.TvEpisode,
                        FileType.Video => MediaType.Movie,
                        _ => MediaType.Other
                    }
                };
            }

            _catalogService.AddAsset(asset);

            _catalogService.AddSource(
                new SourceRecord
                {
                    MediaAssetId = asset.Id,
                    SourceType = SourceType.Filesystem,
                    ExternalId = file.FullPath
                });

            count++;
        }

        _catalogService.SaveToDisk();

        return count;
    }

    /// <summary>
    /// Refreshes only the fields that describe the file itself for an
    /// asset that already exists at this path - deliberately leaves
    /// Title/Year/Season/Episode/EpisodeTitle/Genres/TmdbId/TvdbId/
    /// QualityProfileId/Monitored/Id untouched, since those may carry
    /// Arr-sourced enrichment (or authoritative episode identity) set by a
    /// separate sync step that a plain filesystem rescan has no way to
    /// reproduce, and the Id is what Collections reference by membership.
    /// </summary>
    private async Task RefreshStructuralFieldsAsync(
        MediaAsset existingAsset,
        FileRecord file)
    {
        existingAsset.FileSizeBytes = file.SizeBytes;

        if (file.FileType is FileType.Book or FileType.Audio or FileType.Comic)
        {
            return;
        }

        var metadata =
            await _metadataExtractor.ExtractAsync(
                file.FullPath);

        if (metadata is null)
        {
            return;
        }

        existingAsset.Resolution = metadata.Resolution;
        existingAsset.Width = metadata.Width;
        existingAsset.Height = metadata.Height;
        existingAsset.VideoCodec = metadata.Codec;
    }
}






