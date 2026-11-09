using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class MediaCatalogPopulationService
    : IMediaCatalogPopulationService
{
    private readonly IMediaCatalogService _catalogService;

    private readonly IReleaseParser _releaseParser;

    private readonly IMediaMetadataExtractor _metadataExtractor;

    public MediaCatalogPopulationService(
        IMediaCatalogService catalogService,
        IReleaseParser releaseParser,
        IMediaMetadataExtractor metadataExtractor)
    {
        _catalogService = catalogService;
        _releaseParser = releaseParser;
        _metadataExtractor = metadataExtractor;
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

            // Books, audiobooks, music and comics are described by tags
            // inside the file; their definition knows how to read them.
            // Anything else is video, described by its release name.
            var definition =
                MediaTypeDefinitions.ForFile(file.FileType, mediaTypeOverride);

            var asset =
                definition is not null
                    ? CreateTaggedAsset(definition, file)
                    : await CreateVideoAssetAsync(file);

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

    private static MediaAsset CreateTaggedAsset(
        MediaTypeDefinition definition,
        FileRecord file)
    {
        var metadata =
            definition.ReadMetadata(file.FullPath);

        return new MediaAsset
        {
            Title = metadata.Title,
            Author = metadata.Author,
            Album = metadata.Album,
            TrackNumber = metadata.TrackNumber,
            Year = metadata.Year,
            CanonicalPath = file.FullPath,
            FileSizeBytes = file.SizeBytes,
            MediaType = definition.MediaType
        };
    }

    private async Task<MediaAsset> CreateVideoAssetAsync(
        FileRecord file)
    {
        var parsed =
            _releaseParser.Parse(
                Path.GetFileNameWithoutExtension(
                    file.FileName));

        var metadata =
            await _metadataExtractor.ExtractAsync(
                file.FullPath);

        return new MediaAsset
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

            MediaType = file.FileType switch
            {
                FileType.Video when parsed.IsTv => MediaType.TvEpisode,
                FileType.Video => MediaType.Movie,
                _ => MediaType.Other
            }
        };
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
        var sizeUnchanged =
            existingAsset.FileSizeBytes == file.SizeBytes;

        existingAsset.FileSizeBytes = file.SizeBytes;

        if (file.FileType is FileType.Book or FileType.Audio or FileType.Comic)
        {
            return;
        }

        // ffprobe is a separate program launched once per file. A video
        // that is the same size as last time and already has its
        // dimensions on record hasn't changed, so don't probe it again -
        // on a rescan that is nearly every file in the library.
        if (sizeUnchanged && existingAsset.Height > 0)
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
