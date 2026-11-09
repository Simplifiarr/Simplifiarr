using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class AudiobookPromotionCandidateService
    : IAudiobookPromotionCandidateService
{
    private readonly ISettingsService _settingsService;
    private readonly IFileInventoryService _fileInventory;
    private readonly IAudiobookMetadataExtractor _audiobookMetadataExtractor;
    private readonly IMediaCatalogService _catalogService;
    private readonly IAudiobookPromotionCandidateCacheService _candidateCache;
    private readonly IAudiobookLibraryTargetResolver _libraryTargetResolver;
    private readonly IMediaCatalogPopulationService _catalogPopulationService;
    private readonly IIgnoredItemsService _ignoredItemsService;

    public AudiobookPromotionCandidateService(
        ISettingsService settingsService,
        IFileInventoryService fileInventory,
        IAudiobookMetadataExtractor audiobookMetadataExtractor,
        IMediaCatalogService catalogService,
        IAudiobookPromotionCandidateCacheService candidateCache,
        IAudiobookLibraryTargetResolver libraryTargetResolver,
        IMediaCatalogPopulationService catalogPopulationService,
        IIgnoredItemsService ignoredItemsService)
    {
        _settingsService = settingsService;
        _fileInventory = fileInventory;
        _audiobookMetadataExtractor = audiobookMetadataExtractor;
        _catalogService = catalogService;
        _candidateCache = candidateCache;
        _libraryTargetResolver = libraryTargetResolver;
        _catalogPopulationService = catalogPopulationService;
        _ignoredItemsService = ignoredItemsService;
    }

    public async Task<List<AudiobookPromotionCandidate>>
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
                    !string.IsNullOrWhiteSpace(profile.AudiobookLibraryPath) &&
                    Directory.Exists(profile.AudiobookLibraryPath))
                .ToList();

        foreach (var profile in libraryProfiles)
        {
            var files =
                await _fileInventory.GetInventoryAsync(
                    profile.AudiobookLibraryPath,
                    forceRefresh: true);

            var audiobookFiles =
                files
                    .Where(file => file.FileType == FileType.Audio)
                    .ToList();

            await _catalogPopulationService.PopulateAsync(
                audiobookFiles,
                MediaType.Audiobook);

            _catalogService.RemoveAssetsUnderPathExcept(
                profile.AudiobookLibraryPath,
                audiobookFiles.Select(file => file.FullPath).ToList());
        }

        var results =
            new List<AudiobookPromotionCandidate>();

        var downloadPaths =
            profiles
                .Select(profile => profile.AudiobookDownloadPath)
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
                file.FileType == FileType.Audio))
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
                            profile.AudiobookDownloadPath));

                if (profile is null ||
                    string.IsNullOrWhiteSpace(profile.AudiobookLibraryPath))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(profile.SeedOnlyPath) &&
                    PathContainment.IsPathUnderRoot(file.FullPath, profile.SeedOnlyPath))
                {
                    continue;
                }

                var metadata =
                    _audiobookMetadataExtractor.Extract(
                        file.FullPath);

                var format =
                    Path.GetExtension(file.FullPath)
                        .TrimStart('.');

                var existingAsset =
                    _catalogService.FindAudiobookAsset(
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
                        profile.AudiobookLibraryPath,
                        Path.GetFileName(file.FullPath),
                        profile.PreserveOriginalFileName);

                if (string.IsNullOrWhiteSpace(target.TargetFilePath))
                {
                    continue;
                }

                results.Add(
                    new AudiobookPromotionCandidate
                    {
                        SourcePath = file.FullPath,
                        Title = metadata.Title,
                        Author = metadata.Author,
                        Narrator = metadata.Narrator,
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
                                metadata.Source == AudiobookMetadataSource.Embedded
                                    ? "New audiobook identified from embedded metadata."
                                    : "New audiobook identified from filename (no embedded metadata found)."
                        },
                        CanPromote = true,
                        Reason =
                            metadata.Source == AudiobookMetadataSource.Embedded
                                ? "New audiobook identified from embedded metadata."
                                : "New audiobook identified from filename (no embedded metadata found)."
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
