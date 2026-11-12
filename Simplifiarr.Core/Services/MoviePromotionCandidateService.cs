using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class MoviePromotionCandidateService
    : IMoviePromotionCandidateService
{
    private readonly ISettingsService _settingsService;
    private readonly IFileInventoryService _fileInventory;
    private readonly IReleaseParser _releaseParser;
    private readonly IPromotionPlanService _promotionPlanService;
    private readonly IQbittorrentScanner _torrentScanner;
    private readonly IMediaCatalogService _catalogService;
    private readonly IPromotionCandidateCacheService _candidateCache;
    private readonly IMediaCatalogPopulationService
        _catalogPopulationService;
    private readonly ITorrentCatalogPopulationService
        _torrentCatalogPopulationService;
    private readonly IIgnoredItemsService _ignoredItemsService;
    private readonly IHistoryService _historyService;
    private readonly IArrNamingService _arrNaming;

    public MoviePromotionCandidateService(
        ISettingsService settingsService,
        IFileInventoryService fileInventory,
        IReleaseParser releaseParser,
        IPromotionPlanService promotionPlanService,
        IQbittorrentScanner torrentScanner,
        IMediaCatalogService catalogService,
        IPromotionCandidateCacheService candidateCache,
        IMediaCatalogPopulationService catalogPopulationService,
        ITorrentCatalogPopulationService torrentCatalogPopulationService,
        IIgnoredItemsService ignoredItemsService,
        IHistoryService historyService,
        IArrNamingService arrNaming)
    {
        _settingsService = settingsService;
        _fileInventory = fileInventory;
        _releaseParser = releaseParser;
        _promotionPlanService = promotionPlanService;
        _torrentScanner = torrentScanner;
        _catalogService = catalogService;
        _candidateCache = candidateCache;
        _catalogPopulationService = catalogPopulationService;
        _torrentCatalogPopulationService =
            torrentCatalogPopulationService;
        _ignoredItemsService = ignoredItemsService;
        _historyService = historyService;
        _arrNaming = arrNaming;
    }

    public async Task<List<MoviePromotionCandidate>>
        GetCandidatesAsync(
            string? driveName = null,
            bool forceRefresh = false,
            MediaKind mediaKind = MediaKind.All)
    {
        var settings =
            await _settingsService.GetSettingsAsync();

        var profiles =
            settings.DriveProfiles
                .Where(profile =>
                    profile.Enabled &&
                    (string.IsNullOrWhiteSpace(driveName) ||
                     string.Equals(
                         profile.Name,
                         driveName,
                         StringComparison.OrdinalIgnoreCase)))
                .ToList();

        if (!forceRefresh)
        {
            return profiles
                .SelectMany(profile =>
                    _candidateCache.GetCandidates(profile.Name))
                .Where(candidate => MatchesKind(candidate, mediaKind))
                .GroupBy(
                    candidate => candidate.SourcePath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(candidate => candidate.Title)
                .ThenBy(candidate => candidate.Year)
                .ToList();
        }

        var downloadPaths =
            profiles
                .Select(profile => profile.DownloadPath)
                .Where(path =>
                    !string.IsNullOrWhiteSpace(path) &&
                    Directory.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        foreach (var profile in profiles)
        {
            foreach (var libraryPath in GetLibraryPaths(profile, mediaKind))
            {
                if (!Directory.Exists(libraryPath))
                {
                    continue;
                }

                var files =
                    await _fileInventory.GetInventoryAsync(
                        libraryPath,
                        forceRefresh: true);

                await _catalogPopulationService.PopulateAsync(files);

                _catalogService.RemoveAssetsUnderPathExcept(
                    libraryPath,
                    files.Select(file => file.FullPath).ToList());
            }
        }

        var torrents =
            await _torrentScanner.GetTorrentsAsync();

        await _torrentCatalogPopulationService.PopulateAsync(
            torrents);

        var history =
            await _historyService.GetHistoryAsync();

        var previousPromotions =
            history
                .Where(record =>
                    record.ActionType == HistoryActionType.PromotionSucceeded)
                .GroupBy(
                    record => record.DownloadPath,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group =>
                        group
                            .OrderByDescending(record => record.TimestampUtc)
                            .First(),
                    StringComparer.OrdinalIgnoreCase);

        var results =
            new List<MoviePromotionCandidate>();

        var processedPaths =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var downloadPath in downloadPaths)
        {
            var files =
                await _fileInventory.GetInventoryAsync(
                    downloadPath,
                    forceRefresh: true);

            foreach (var file in files.Where(file =>
                file.FileType == FileType.Video &&
                !SampleFileDetector.IsSample(file)))
            {
                if (!File.Exists(file.FullPath) ||
                    !processedPaths.Add(file.FullPath))
                {
                    continue;
                }

                if (_ignoredItemsService.IsIgnored(file.FullPath))
                {
                    continue;
                }

                var owningProfile =
                    profiles
                        .FirstOrDefault(profile =>
                            PathContainment.IsPathUnderRoot(
                                file.FullPath,
                                profile.DownloadPath));

                if (owningProfile is not null &&
                    !string.IsNullOrWhiteSpace(owningProfile.SeedOnlyPath) &&
                    PathContainment.IsPathUnderRoot(file.FullPath, owningProfile.SeedOnlyPath))
                {
                    continue;
                }

                var release =
                    _releaseParser.Parse(
                        Path.GetFileNameWithoutExtension(
                            file.FileName));

                // Some release groups reverse their naming convention on the
                // actual video file (e.g. "grp-some.movie.2023.bdrip.mkv"
                // instead of the usual trailing "-GRP"), which the parser
                // can't distinguish from a real title. The containing release
                // folder almost always keeps the proper "Title.Year.Quality
                // -GROUP" form even when the file inside doesn't, so prefer
                // it for movies when it looks like a real release name.
                if (!release.IsTv)
                {
                    var fileDirectory =
                        Path.GetDirectoryName(file.FullPath);

                    if (!string.IsNullOrWhiteSpace(fileDirectory) &&
                        !IsSameDirectory(fileDirectory, downloadPath))
                    {
                        var folderName =
                            Path.GetFileName(fileDirectory);

                        if (!string.IsNullOrWhiteSpace(folderName))
                        {
                            var folderRelease =
                                _releaseParser.Parse(folderName);

                            if (!folderRelease.IsTv &&
                                folderRelease.Year.HasValue &&
                                !string.IsNullOrWhiteSpace(
                                    folderRelease.NormalizedTitle))
                            {
                                release = folderRelease;
                            }
                        }
                    }
                }

                // Sonarr's or Radarr's own reading of the name, and their
                // folder for it if they already have it.
                release =
                    await _arrNaming.IdentifyAsync(release);

                if (mediaKind == MediaKind.Movies && release.IsTv)
                {
                    continue;
                }

                if (mediaKind == MediaKind.Tv && !release.IsTv)
                {
                    continue;
                }

                var plan =
                    await _promotionPlanService.CreateAsync(
                        release,
                        file.FullPath);

                if (!plan.CanPromote)
                {
                    continue;
                }

                var driveProfileName =
                    owningProfile?.Name ?? string.Empty;

                var wasPreviouslyPromoted =
                    previousPromotions.TryGetValue(
                        file.FullPath,
                        out var previousRecord);

                results.Add(
                    new MoviePromotionCandidate
                    {
                        SourcePath = file.FullPath,
                        Title = release.NormalizedTitle,
                        Year = release.Year,
                        IsTv = release.IsTv,
                        Season = release.Season,
                        Episode = release.Episode,
                        DiscoveredUtc = DateTime.UtcNow,
                        DriveProfileName = driveProfileName,
                        Plan = plan,
                        CanPromote = true,
                        Reason = plan.Reason,
                        WasPreviouslyPromoted = wasPreviouslyPromoted,
                        PreviousLibraryPath =
                            wasPreviouslyPromoted
                                ? previousRecord!.LibraryPath
                                : null
                    });
            }
        }

        var sortedResults =
            results
                .OrderBy(candidate => candidate.Title)
                .ThenBy(candidate => candidate.Year)
                .ToList();

        foreach (var profile in profiles)
        {
            var newProfileCandidates =
                sortedResults
                    .Where(candidate =>
                        PathContainment.IsPathUnderRoot(
                            candidate.SourcePath,
                            profile.DownloadPath))
                    .ToList();

            // Only replace the cache entries for the kind(s) just rescanned -
            // otherwise scanning Movies alone would wipe previously-cached TV
            // candidates for this profile (and vice versa).
            var existingCandidates =
                _candidateCache.GetCandidates(profile.Name);

            var preserved =
                mediaKind switch
                {
                    MediaKind.Movies => existingCandidates.Where(candidate => candidate.IsTv),
                    MediaKind.Tv => existingCandidates.Where(candidate => !candidate.IsTv),
                    _ => Enumerable.Empty<MoviePromotionCandidate>()
                };

            var mergedCandidates =
                preserved
                    .Concat(newProfileCandidates)
                    .GroupBy(
                        candidate => candidate.SourcePath,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

            _candidateCache.SetCandidates(
                profile.Name,
                mergedCandidates);

            _candidateCache.SetLastScanUtc(
                profile.Name,
                mediaKind);
        }

        return sortedResults;
    }

    private static bool MatchesKind(
        MoviePromotionCandidate candidate,
        MediaKind mediaKind)
    {
        return mediaKind switch
        {
            MediaKind.Movies => !candidate.IsTv,
            MediaKind.Tv => candidate.IsTv,
            _ => true
        };
    }


    private static bool IsSameDirectory(
        string first,
        string second)
    {
        var normalizedFirst =
            Path.GetFullPath(first)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

        var normalizedSecond =
            Path.GetFullPath(second)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

        return normalizedFirst.Equals(
            normalizedSecond,
            StringComparison.OrdinalIgnoreCase);
    }


    private static IEnumerable<string> GetLibraryPaths(
        DriveProfile profile,
        MediaKind mediaKind)
    {
        var paths =
            mediaKind switch
            {
                MediaKind.Movies => new[] { profile.MovieLibraryPath },
                MediaKind.Tv => new[] { profile.TvLibraryPath },
                _ => new[] { profile.MovieLibraryPath, profile.TvLibraryPath }
            };

        return paths.Where(path =>
            !string.IsNullOrWhiteSpace(path));
    }
}
