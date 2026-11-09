using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class MusicPromotionCandidateService
    : IMusicPromotionCandidateService
{
    private readonly ISettingsService _settingsService;
    private readonly IFileInventoryService _fileInventory;
    private readonly IMusicMetadataExtractor _musicMetadataExtractor;
    private readonly IMediaCatalogService _catalogService;
    private readonly IMusicPromotionCandidateCacheService _candidateCache;
    private readonly IMusicLibraryTargetResolver _libraryTargetResolver;
    private readonly IMediaCatalogPopulationService _catalogPopulationService;
    private readonly IIgnoredItemsService _ignoredItemsService;

    public MusicPromotionCandidateService(
        ISettingsService settingsService,
        IFileInventoryService fileInventory,
        IMusicMetadataExtractor musicMetadataExtractor,
        IMediaCatalogService catalogService,
        IMusicPromotionCandidateCacheService candidateCache,
        IMusicLibraryTargetResolver libraryTargetResolver,
        IMediaCatalogPopulationService catalogPopulationService,
        IIgnoredItemsService ignoredItemsService)
    {
        _settingsService = settingsService;
        _fileInventory = fileInventory;
        _musicMetadataExtractor = musicMetadataExtractor;
        _catalogService = catalogService;
        _candidateCache = candidateCache;
        _libraryTargetResolver = libraryTargetResolver;
        _catalogPopulationService = catalogPopulationService;
        _ignoredItemsService = ignoredItemsService;
    }

    public async Task<List<MusicPromotionCandidate>>
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
                    !string.IsNullOrWhiteSpace(profile.MusicLibraryPath) &&
                    Directory.Exists(profile.MusicLibraryPath))
                .ToList();

        foreach (var profile in libraryProfiles)
        {
            var files =
                await _fileInventory.GetInventoryAsync(
                    profile.MusicLibraryPath,
                    forceRefresh: true);

            var musicFiles =
                files
                    .Where(file => file.FileType == FileType.Audio)
                    .ToList();

            await _catalogPopulationService.PopulateAsync(
                musicFiles,
                MediaType.Music);

            _catalogService.RemoveAssetsUnderPathExcept(
                profile.MusicLibraryPath,
                musicFiles.Select(file => file.FullPath).ToList());
        }

        var results =
            new List<MusicPromotionCandidate>();

        var downloadPaths =
            profiles
                .Select(profile => profile.MusicDownloadPath)
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
                            profile.MusicDownloadPath));

                if (profile is null ||
                    string.IsNullOrWhiteSpace(profile.MusicLibraryPath))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(profile.SeedOnlyPath) &&
                    PathContainment.IsPathUnderRoot(file.FullPath, profile.SeedOnlyPath))
                {
                    continue;
                }

                var metadata =
                    _musicMetadataExtractor.Extract(
                        file.FullPath);

                var format =
                    Path.GetExtension(file.FullPath)
                        .TrimStart('.');

                var existingAsset =
                    _catalogService.FindMusicAsset(
                        metadata.Title,
                        metadata.Artist,
                        metadata.Album,
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
                        profile.MusicLibraryPath,
                        Path.GetFileName(file.FullPath),
                        profile.PreserveOriginalFileName);

                if (string.IsNullOrWhiteSpace(target.TargetFilePath))
                {
                    continue;
                }

                results.Add(
                    new MusicPromotionCandidate
                    {
                        SourcePath = file.FullPath,
                        Title = metadata.Title,
                        Artist = metadata.Artist,
                        Album = metadata.Album,
                        TrackNumber = metadata.TrackNumber,
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
                                metadata.Source == MusicMetadataSource.Embedded
                                    ? "New track identified from embedded metadata."
                                    : "New track identified from filename (no embedded metadata found)."
                        },
                        CanPromote = true,
                        Reason =
                            metadata.Source == MusicMetadataSource.Embedded
                                ? "New track identified from embedded metadata."
                                : "New track identified from filename (no embedded metadata found)."
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
