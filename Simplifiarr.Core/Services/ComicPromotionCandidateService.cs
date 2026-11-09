using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class ComicPromotionCandidateService
    : IComicPromotionCandidateService
{
    private readonly ISettingsService _settingsService;
    private readonly IFileInventoryService _fileInventory;
    private readonly IComicMetadataExtractor _comicMetadataExtractor;
    private readonly IMediaCatalogService _catalogService;
    private readonly IComicPromotionCandidateCacheService _candidateCache;
    private readonly IComicLibraryTargetResolver _libraryTargetResolver;
    private readonly IMediaCatalogPopulationService _catalogPopulationService;
    private readonly IIgnoredItemsService _ignoredItemsService;

    public ComicPromotionCandidateService(
        ISettingsService settingsService,
        IFileInventoryService fileInventory,
        IComicMetadataExtractor comicMetadataExtractor,
        IMediaCatalogService catalogService,
        IComicPromotionCandidateCacheService candidateCache,
        IComicLibraryTargetResolver libraryTargetResolver,
        IMediaCatalogPopulationService catalogPopulationService,
        IIgnoredItemsService ignoredItemsService)
    {
        _settingsService = settingsService;
        _fileInventory = fileInventory;
        _comicMetadataExtractor = comicMetadataExtractor;
        _catalogService = catalogService;
        _candidateCache = candidateCache;
        _libraryTargetResolver = libraryTargetResolver;
        _catalogPopulationService = catalogPopulationService;
        _ignoredItemsService = ignoredItemsService;
    }

    public async Task<List<ComicPromotionCandidate>>
        GetCandidatesAsync(
            string? driveName = null,
            bool forceRefresh = false)
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
                .GroupBy(
                    candidate => candidate.SourcePath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(candidate => candidate.Title)
                .ToList();
        }

        var libraryProfiles =
            profiles
                .Where(profile =>
                    !string.IsNullOrWhiteSpace(profile.ComicLibraryPath) &&
                    Directory.Exists(profile.ComicLibraryPath))
                .ToList();

        foreach (var profile in libraryProfiles)
        {
            var files =
                await _fileInventory.GetInventoryAsync(
                    profile.ComicLibraryPath,
                    forceRefresh: true);

            var comicFiles =
                files
                    .Where(file => file.FileType == FileType.Comic)
                    .ToList();

            await _catalogPopulationService.PopulateAsync(comicFiles);

            _catalogService.RemoveAssetsUnderPathExcept(
                profile.ComicLibraryPath,
                comicFiles.Select(file => file.FullPath).ToList());
        }

        var results =
            new List<ComicPromotionCandidate>();

        var downloadPaths =
            profiles
                .Select(profile => profile.ComicDownloadPath)
                .Where(path =>
                    !string.IsNullOrWhiteSpace(path) &&
                    Directory.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var processedPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var downloadPath in downloadPaths)
        {
            var files =
                await _fileInventory.GetInventoryAsync(
                    downloadPath,
                    forceRefresh: true);

            foreach (var file in files.Where(file =>
                file.FileType == FileType.Comic))
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

                var profile =
                    profiles.FirstOrDefault(profile =>
                        PathContainment.IsPathUnderRoot(
                            file.FullPath,
                            profile.ComicDownloadPath));

                if (profile is null ||
                    string.IsNullOrWhiteSpace(profile.ComicLibraryPath))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(profile.SeedOnlyPath) &&
                    PathContainment.IsPathUnderRoot(file.FullPath, profile.SeedOnlyPath))
                {
                    continue;
                }

                var metadata =
                    _comicMetadataExtractor.Extract(
                        file.FullPath);

                var format =
                    Path.GetExtension(file.FullPath)
                        .TrimStart('.');

                var existingAsset =
                    _catalogService.FindComicAsset(
                        metadata.Title,
                        metadata.Series,
                        metadata.Issue,
                        format);

                if (existingAsset is not null &&
                    File.Exists(existingAsset.CanonicalPath))
                {
                    continue;
                }

                var target =
                    _libraryTargetResolver.Resolve(
                        metadata,
                        format,
                        profile.ComicLibraryPath,
                        Path.GetFileName(file.FullPath),
                        profile.PreserveOriginalFileName);

                if (string.IsNullOrWhiteSpace(target.TargetFilePath))
                {
                    continue;
                }

                results.Add(
                    new ComicPromotionCandidate
                    {
                        SourcePath = file.FullPath,
                        Title = metadata.Title,
                        Series = metadata.Series,
                        Issue = metadata.Issue,
                        Author = metadata.Author,
                        Year = metadata.Year,
                        Format = format,
                        DiscoveredUtc = DateTime.UtcNow,
                        DriveProfileName = profile.Name,
                        Plan = new PromotionPlan
                        {
                            SourcePath = file.FullPath,
                            TargetDirectory = target.TargetDirectory,
                            TargetFilePath = target.TargetFilePath,
                            CanPromote = true,
                            Reason =
                                metadata.Source == ComicMetadataSource.Embedded
                                    ? "New comic identified from embedded ComicInfo.xml."
                                    : "New comic identified from filename (no embedded metadata found)."
                        },
                        CanPromote = true,
                        Reason =
                            metadata.Source == ComicMetadataSource.Embedded
                                ? "New comic identified from embedded ComicInfo.xml."
                                : "New comic identified from filename (no embedded metadata found)."
                    });
            }
        }

        var sortedResults =
            results
                .OrderBy(candidate => candidate.Title)
                .ToList();

        foreach (var profile in profiles)
        {
            var profileCandidates =
                sortedResults
                    .Where(candidate =>
                        string.Equals(
                            candidate.DriveProfileName,
                            profile.Name,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();

            _candidateCache.SetCandidates(
                profile.Name,
                profileCandidates);
        }

        return sortedResults;
    }
}
