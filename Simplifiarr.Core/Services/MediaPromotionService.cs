using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Finds downloaded books, audiobooks, music and comics that aren't in their
/// library yet. One implementation for every type; what differs between
/// types lives in its <see cref="MediaTypeDefinition"/>.
/// </summary>
public interface IMediaPromotionService
{
    /// <summary>
    /// Looks again, for one drive or (when <paramref name="driveName"/> is
    /// blank) every enabled drive, and remembers what it found.
    /// </summary>
    Task<List<MediaPromotionCandidate>> ScanAsync(
        MediaType mediaType,
        string? driveName = null);

    /// <summary>
    /// What the last scan found for a drive. Does not touch the disk.
    /// </summary>
    IReadOnlyList<MediaPromotionCandidate> GetCandidates(
        MediaType mediaType,
        string driveName);

    DateTime? GetLastScanUtc(
        MediaType mediaType,
        string driveName);

    /// <summary>
    /// Forgets a candidate, after it has been promoted or ignored.
    /// </summary>
    void RemoveCandidate(
        MediaType mediaType,
        string sourcePath);
}

public class MediaPromotionService
    : IMediaPromotionService
{
    private readonly ISettingsService _settingsService;
    private readonly IFileInventoryService _fileInventory;
    private readonly IMediaCatalogService _catalogService;
    private readonly IMediaCatalogPopulationService _catalogPopulationService;
    private readonly IIgnoredItemsService _ignoredItemsService;

    private readonly Dictionary<MediaType, DriveCandidateCache<MediaPromotionCandidate>> _caches;

    // Reading tags means opening the file. A download that hasn't changed
    // since the last scan has the same tags it had then, so remember them
    // for as long as the app is running.
    private readonly Dictionary<string, RememberedMetadata> _metadataMemory =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _memorySync = new();

    public MediaPromotionService(
        ISettingsService settingsService,
        IFileInventoryService fileInventory,
        IMediaCatalogService catalogService,
        IMediaCatalogPopulationService catalogPopulationService,
        IIgnoredItemsService ignoredItemsService)
    {
        _settingsService = settingsService;
        _fileInventory = fileInventory;
        _catalogService = catalogService;
        _catalogPopulationService = catalogPopulationService;
        _ignoredItemsService = ignoredItemsService;

        _caches =
            MediaTypeDefinitions.All.ToDictionary(
                definition => definition.MediaType,
                definition =>
                    new DriveCandidateCache<MediaPromotionCandidate>(
                        definition.CacheFileName,
                        candidate => candidate.SourcePath,
                        candidate =>
                            !string.IsNullOrWhiteSpace(candidate.SourcePath) &&
                            File.Exists(candidate.SourcePath)));
    }

    public IReadOnlyList<MediaPromotionCandidate> GetCandidates(
        MediaType mediaType,
        string driveName) =>
        _caches[mediaType].GetCandidates(driveName);

    public DateTime? GetLastScanUtc(
        MediaType mediaType,
        string driveName) =>
        _caches[mediaType].GetLastScanUtc(driveName);

    public void RemoveCandidate(
        MediaType mediaType,
        string sourcePath) =>
        _caches[mediaType].RemoveCandidate(sourcePath);

    public async Task<List<MediaPromotionCandidate>> ScanAsync(
        MediaType mediaType,
        string? driveName = null)
    {
        var definition = MediaTypeDefinitions.Get(mediaType);

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

        await RefreshLibrariesInCatalogAsync(definition, profiles);

        var results =
            new List<MediaPromotionCandidate>();

        var downloadPaths =
            profiles
                .Select(definition.GetDownloadPath)
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
                         file.FileType == definition.FileType))
            {
                var candidate =
                    TryCreateCandidate(
                        definition,
                        file,
                        profiles,
                        processedPaths);

                if (candidate is not null)
                {
                    results.Add(candidate);
                }
            }
        }

        var sortedResults =
            results
                .OrderBy(candidate => candidate.Title)
                .ToList();

        foreach (var profile in profiles)
        {
            _caches[mediaType].SetCandidates(
                profile.Name,
                sortedResults
                    .Where(candidate =>
                        string.Equals(
                            candidate.DriveProfileName,
                            profile.Name,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList());
        }

        return sortedResults;
    }

    /// <summary>
    /// Brings the catalog up to date with what is in each library folder
    /// right now, so "is this already in the library?" is answered against
    /// the present, not the last scan.
    /// </summary>
    private async Task RefreshLibrariesInCatalogAsync(
        MediaTypeDefinition definition,
        List<DriveProfile> profiles)
    {
        foreach (var profile in profiles)
        {
            var libraryPath = definition.GetLibraryPath(profile);

            if (string.IsNullOrWhiteSpace(libraryPath) ||
                !Directory.Exists(libraryPath))
            {
                continue;
            }

            var files =
                await _fileInventory.GetInventoryAsync(
                    libraryPath,
                    forceRefresh: true);

            var libraryFiles =
                files
                    .Where(file => file.FileType == definition.FileType)
                    .ToList();

            await _catalogPopulationService.PopulateAsync(
                libraryFiles,
                definition.CatalogTypeOverride);

            _catalogService.RemoveAssetsUnderPathExcept(
                libraryPath,
                libraryFiles.Select(file => file.FullPath).ToList());
        }
    }

    private MediaPromotionCandidate? TryCreateCandidate(
        MediaTypeDefinition definition,
        FileRecord file,
        List<DriveProfile> profiles,
        HashSet<string> processedPaths)
    {
        if (!File.Exists(file.FullPath) ||
            !processedPaths.Add(file.FullPath))
        {
            return null;
        }

        if (_ignoredItemsService.IsIgnored(file.FullPath))
        {
            return null;
        }

        var profile =
            profiles.FirstOrDefault(profile =>
                PathContainment.IsPathUnderRoot(
                    file.FullPath,
                    definition.GetDownloadPath(profile)));

        if (profile is null)
        {
            return null;
        }

        var libraryPath = definition.GetLibraryPath(profile);

        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            return null;
        }

        // Seed-only downloads are kept purely for seeding and never belong
        // in a library.
        if (!string.IsNullOrWhiteSpace(profile.SeedOnlyPath) &&
            PathContainment.IsPathUnderRoot(file.FullPath, profile.SeedOnlyPath))
        {
            return null;
        }

        var metadata = ReadMetadata(definition, file);

        var format =
            Path.GetExtension(file.FullPath)
                .TrimStart('.');

        var existingAsset =
            definition.FindInCatalog(_catalogService, metadata, format);

        if (existingAsset is not null &&
            File.Exists(existingAsset.CanonicalPath))
        {
            return null;
        }

        var target =
            definition.ResolveTarget(
                metadata,
                format,
                libraryPath,
                Path.GetFileName(file.FullPath),
                profile.PreserveOriginalFileName);

        if (string.IsNullOrWhiteSpace(target.TargetFilePath))
        {
            return null;
        }

        // Where this would go is already occupied, so the library has it -
        // even if the catalog match above missed. That happens with files
        // that have no tags: the download is "Author - Title.epub" but the
        // library copy is just "Title.epub", so the two read as different
        // authors. Offering it again would only fail with "a file already
        // exists" when promoted.
        if (target.FileExists)
        {
            return null;
        }

        var reason = definition.DescribeNewItem(metadata);

        return new MediaPromotionCandidate
        {
            MediaType = definition.MediaType,
            SourcePath = file.FullPath,
            Title = metadata.Title,
            Author = metadata.Author,
            Narrator = metadata.Narrator,
            Album = metadata.Album,
            TrackNumber = metadata.TrackNumber,
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
                Reason = reason
            },
            CanPromote = true,
            Reason = reason
        };
    }

    private MediaFileMetadata ReadMetadata(
        MediaTypeDefinition definition,
        FileRecord file)
    {
        // Audiobooks and music can be the very same file read two ways, so
        // the type is part of what is remembered.
        var key = $"{definition.MediaType}|{file.FullPath}";

        lock (_memorySync)
        {
            if (_metadataMemory.TryGetValue(key, out var remembered) &&
                remembered.SizeBytes == file.SizeBytes &&
                remembered.LastModifiedUtc == file.LastModifiedUtc)
            {
                return remembered.Metadata;
            }
        }

        var metadata = definition.ReadMetadata(file.FullPath);

        lock (_memorySync)
        {
            _metadataMemory[key] =
                new RememberedMetadata(
                    file.SizeBytes,
                    file.LastModifiedUtc,
                    metadata);
        }

        return metadata;
    }

    private sealed record RememberedMetadata(
        long SizeBytes,
        DateTime LastModifiedUtc,
        MediaFileMetadata Metadata);
}
