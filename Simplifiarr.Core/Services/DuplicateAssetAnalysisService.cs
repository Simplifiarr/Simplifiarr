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
                .GroupBy(BuildKey)
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
    /// Drops files marked "not a duplicate"; a title left with a single copy
    /// is no longer a duplicate. Shared by the Duplicates page and Home so
    /// the two always agree.
    /// </summary>
    public static List<DuplicateAssetGroup> RemoveIgnored(
        List<DuplicateAssetGroup> groups,
        IIgnoredItemsService ignored)
    {
        foreach (var group in groups)
        {
            group.Entries.RemoveAll(entry =>
                ignored.IsIgnored(entry.CanonicalPath));
        }

        groups.RemoveAll(group => group.Entries.Count <= 1);

        return groups;
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
            NormalizeTitle(asset.Title),
            asset.Year,
            asset.Season,
            asset.Episode,
            NormalizeTitle(asset.Author),
            asset.MediaType is MediaType.Music or MediaType.Comic ? NormalizeTitle(asset.Album) : string.Empty,
            asset.MediaType == MediaType.Comic ? asset.TrackNumber : null);
    }

    /// <summary>
    /// The same title is written differently depending on where it came
    /// from: Sonarr says "Night Shift: Origins", a file name says
    /// "Night Shift - Origins".
    /// Compares letters and numbers only, ignoring case, punctuation and
    /// spacing, with "&amp;" read as "and".
    /// </summary>
    public static string NormalizeTitle(
        string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(title.Length);
        var pendingSpace = false;

        foreach (var character in title.Replace("&", " and ", StringComparison.Ordinal))
        {
            // An apostrophe is part of the word: "Don't" and "Don’t" are
            // both "dont", not "don t".
            if (character is '\'' or '’' or '‘' or '`')
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(character));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    private readonly record struct DuplicateKey(
        MediaType MediaType,
        string Title,
        int? Year,
        int? Season,
        int? Episode,
        string Author,
        string Album,
        double? Issue);
}
