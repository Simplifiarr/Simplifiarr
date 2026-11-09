using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class BookPromotionCandidateService
    : IBookPromotionCandidateService
{
    private readonly ISettingsService _settingsService;
    private readonly IFileInventoryService _fileInventory;
    private readonly IBookMetadataExtractor _bookMetadataExtractor;
    private readonly IMediaCatalogService _catalogService;
    private readonly IBookPromotionCandidateCacheService _candidateCache;
    private readonly IBookLibraryTargetResolver _libraryTargetResolver;
    private readonly IMediaCatalogPopulationService _catalogPopulationService;
    private readonly IIgnoredItemsService _ignoredItemsService;

    public BookPromotionCandidateService(
        ISettingsService settingsService,
        IFileInventoryService fileInventory,
        IBookMetadataExtractor bookMetadataExtractor,
        IMediaCatalogService catalogService,
        IBookPromotionCandidateCacheService candidateCache,
        IBookLibraryTargetResolver libraryTargetResolver,
        IMediaCatalogPopulationService catalogPopulationService,
        IIgnoredItemsService ignoredItemsService)
    {
        _settingsService = settingsService;
        _fileInventory = fileInventory;
        _bookMetadataExtractor = bookMetadataExtractor;
        _catalogService = catalogService;
        _candidateCache = candidateCache;
        _libraryTargetResolver = libraryTargetResolver;
        _catalogPopulationService = catalogPopulationService;
        _ignoredItemsService = ignoredItemsService;
    }

    public async Task<List<BookPromotionCandidate>>
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
                    !string.IsNullOrWhiteSpace(profile.BookLibraryPath) &&
                    Directory.Exists(profile.BookLibraryPath))
                .ToList();

        foreach (var profile in libraryProfiles)
        {
            var files =
                await _fileInventory.GetInventoryAsync(
                    profile.BookLibraryPath,
                    forceRefresh: true);

            var bookFiles =
                files
                    .Where(file => file.FileType == FileType.Book)
                    .ToList();

            await _catalogPopulationService.PopulateAsync(bookFiles);

            _catalogService.RemoveAssetsUnderPathExcept(
                profile.BookLibraryPath,
                bookFiles.Select(file => file.FullPath).ToList());
        }

        var results =
            new List<BookPromotionCandidate>();

        var downloadPaths =
            profiles
                .Select(profile => profile.DownloadPath)
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
                file.FileType == FileType.Book))
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
                            profile.DownloadPath));

                if (profile is null ||
                    string.IsNullOrWhiteSpace(profile.BookLibraryPath))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(profile.SeedOnlyPath) &&
                    PathContainment.IsPathUnderRoot(file.FullPath, profile.SeedOnlyPath))
                {
                    continue;
                }

                var metadata =
                    _bookMetadataExtractor.Extract(
                        file.FullPath);

                var format =
                    Path.GetExtension(file.FullPath)
                        .TrimStart('.');

                var existingAsset =
                    _catalogService.FindBookAsset(
                        metadata.Title,
                        metadata.Author,
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
                        profile.BookLibraryPath,
                        Path.GetFileName(file.FullPath),
                        profile.PreserveOriginalFileName);

                if (string.IsNullOrWhiteSpace(target.TargetFilePath))
                {
                    continue;
                }

                results.Add(
                    new BookPromotionCandidate
                    {
                        SourcePath = file.FullPath,
                        Title = metadata.Title,
                        Author = metadata.Author,
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
                                metadata.Source == BookMetadataSource.Embedded
                                    ? "New book identified from embedded metadata."
                                    : "New book identified from filename (no embedded metadata found)."
                        },
                        CanPromote = true,
                        Reason =
                            metadata.Source == BookMetadataSource.Embedded
                                ? "New book identified from embedded metadata."
                                : "New book identified from filename (no embedded metadata found)."
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
