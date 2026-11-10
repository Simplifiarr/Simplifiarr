using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class DuplicateAssetAnalysisService
    : IDuplicateAssetAnalysisService
{
    private readonly IHardlinkService _hardlinkService;

    public DuplicateAssetAnalysisService(
        IHardlinkService hardlinkService)
    {
        _hardlinkService = hardlinkService;
    }

    public async Task<List<DuplicateAssetGroup>> FindDuplicatesAsync(
        IReadOnlyCollection<MediaAsset> assets,
        IReadOnlyList<DriveProfile> profiles)
    {
        var relevantAssets =
            assets
                .Where(asset =>
                    asset.MediaType is
                        MediaType.Movie or
                        MediaType.TvEpisode or
                        MediaType.Book or
                        MediaType.Audiobook or
                        MediaType.Music or
                        MediaType.Comic)
                .Where(asset =>
                    !string.IsNullOrWhiteSpace(asset.CanonicalPath))
                // This page is about the same title twice in your
                // libraries. A file sitting in a downloads folder is not a
                // library copy (a download that duplicates a library file
                // is Potential Waste's job), even if a mis-set library
                // folder caused it to be catalogued.
                .Where(asset =>
                    !profiles.Any(profile =>
                        PathContainment.IsPathUnderRoot(
                            asset.CanonicalPath,
                            profile.DownloadPath)))
                .ToList();

        var candidateGroups =
            relevantAssets
                .GroupBy(
                    asset => BuildKey(asset),
                    KeyComparer.Instance)
                .Select(group =>
                    group
                        .GroupBy(
                            asset => asset.CanonicalPath,
                            StringComparer.OrdinalIgnoreCase)
                        .Select(pathGroup => pathGroup.First())
                        .ToList())
                .Where(distinctAssets => distinctAssets.Count > 1)
                .ToList();

        var groups =
            new List<DuplicateAssetGroup>();

        foreach (var candidateAssets in candidateGroups)
        {
            // The catalog is only as fresh as the last scan. A copy that
            // has since been removed (for example from this very page)
            // must not keep its title on the list until the next scan.
            var stillThere =
                candidateAssets
                    .Where(asset => File.Exists(asset.CanonicalPath))
                    .ToList();

            if (stillThere.Count <= 1)
            {
                continue;
            }

            var distinctAssets =
                await CollapseHardlinkedAsync(stillThere);

            if (distinctAssets.Count > 1)
            {
                groups.Add(BuildGroup(distinctAssets, profiles));
            }
        }

        return groups
            .OrderByDescending(group => group.ReclaimableSizeBytes)
            .ToList();
    }

    /// <summary>
    /// Collapses entries that are hardlinks of each other (same underlying
    /// file data, e.g. one copy in the library and another linked into a
    /// Collection or promoted elsewhere) down to one representative entry -
    /// these aren't wasted duplicate storage, just the same file appearing
    /// at more than one intentional path.
    /// </summary>
    private async Task<List<MediaAsset>> CollapseHardlinkedAsync(
        List<MediaAsset> assets)
    {
        var kept =
            new List<MediaAsset>();

        foreach (var asset in assets)
        {
            var isHardlinkOfKept = false;

            foreach (var keptAsset in kept)
            {
                if (await _hardlinkService.AreHardlinkedAsync(
                        asset.CanonicalPath,
                        keptAsset.CanonicalPath))
                {
                    isHardlinkOfKept = true;
                    break;
                }
            }

            if (!isHardlinkOfKept)
            {
                kept.Add(asset);
            }
        }

        return kept;
    }

    private static DuplicateAssetGroup BuildGroup(
        List<MediaAsset> assets,
        IReadOnlyList<DriveProfile> profiles)
    {
        var first = assets[0];

        return new DuplicateAssetGroup
        {
            MediaType = first.MediaType,
            Title = first.Title,
            Year = first.Year,
            Season = first.Season,
            Episode = first.Episode,
            Author = first.Author,
            Entries =
                assets
                    .OrderByDescending(asset => asset.FileSizeBytes)
                    .Select(asset =>
                        new DuplicateAssetEntry
                        {
                            CanonicalPath = asset.CanonicalPath,
                            SizeBytes = asset.FileSizeBytes,
                            Resolution = asset.Resolution,
                            VideoCodec = asset.VideoCodec,
                            DriveProfileName =
                                ResolveDriveProfileName(
                                    asset,
                                    profiles)
                        })
                    .ToList()
        };
    }

    private static string ResolveDriveProfileName(
        MediaAsset asset,
        IReadOnlyList<DriveProfile> profiles)
    {
        var profile =
            profiles.FirstOrDefault(profile =>
                PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.MovieLibraryPath) ||
                PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.TvLibraryPath) ||
                PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.BookLibraryPath) ||
                PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.AudiobookLibraryPath) ||
                PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.MusicLibraryPath) ||
                PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.ComicLibraryPath));

        return profile?.Name ?? string.Empty;
    }

    private static DuplicateKey BuildKey(
        MediaAsset asset)
    {
        return new DuplicateKey(
            asset.MediaType,
            asset.Title,
            asset.Year,
            asset.Season,
            asset.Episode,
            asset.Author,
            asset.MediaType is MediaType.Music or MediaType.Comic ? asset.Album : null,
            asset.MediaType == MediaType.Comic ? asset.TrackNumber : null);
    }

    private readonly record struct DuplicateKey(
        MediaType MediaType,
        string Title,
        int? Year,
        int? Season,
        int? Episode,
        string? Author,
        string? Album,
        double? Issue);

    private sealed class KeyComparer : IEqualityComparer<DuplicateKey>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(DuplicateKey x, DuplicateKey y)
        {
            return x.MediaType == y.MediaType &&
                   x.Year == y.Year &&
                   x.Season == y.Season &&
                   x.Episode == y.Episode &&
                   string.Equals(x.Title, y.Title, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.Author ?? string.Empty, y.Author ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.Album ?? string.Empty, y.Album ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                   x.Issue == y.Issue;
        }

        public int GetHashCode(DuplicateKey obj)
        {
            var hash = new HashCode();
            hash.Add(obj.MediaType);
            hash.Add(obj.Year);
            hash.Add(obj.Season);
            hash.Add(obj.Episode);
            hash.Add(obj.Title, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.Author ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.Album ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.Issue);
            return hash.ToHashCode();
        }
    }
}
