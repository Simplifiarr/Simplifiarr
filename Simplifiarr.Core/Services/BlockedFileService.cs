using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class BlockedFileService
    : IBlockedFileService
{
    private readonly ISettingsService _settingsService;
    private readonly IMediaCatalogService _mediaCatalogService;
    private readonly IBlockedFileCacheService _cache;

    public BlockedFileService(
        ISettingsService settingsService,
        IMediaCatalogService mediaCatalogService,
        IBlockedFileCacheService cache)
    {
        _settingsService = settingsService;
        _mediaCatalogService = mediaCatalogService;
        _cache = cache;
    }

    public async Task<List<BlockedFile>> GetBlockedFilesAsync(
        string? driveName = null,
        bool forceRefresh = false)
    {
        var profiles =
            await ResolveProfilesAsync(driveName);

        if (!forceRefresh)
        {
            return profiles
                .SelectMany(profile => _cache.GetBlockedFiles(profile.Name))
                .OrderByDescending(file => file.SizeBytes)
                .ToList();
        }

        var results =
            new List<BlockedFile>();

        foreach (var profile in profiles)
        {
            var blockedForProfile =
                new List<BlockedFile>();

            foreach (var asset in GetEligibleAssets(profile))
            {
                if (!File.Exists(asset.CanonicalPath) ||
                    !ZoneIdentifierHelper.IsBlocked(asset.CanonicalPath))
                {
                    continue;
                }

                blockedForProfile.Add(new BlockedFile
                {
                    MediaAssetId = asset.Id,
                    Title = asset.Title,
                    MediaType = asset.MediaType,
                    CanonicalPath = asset.CanonicalPath,
                    SizeBytes = asset.FileSizeBytes,
                    DriveProfileName = profile.Name,
                    SourceUrl = ZoneIdentifierHelper.TryReadSourceUrl(asset.CanonicalPath),
                    DiscoveredUtc = DateTime.UtcNow
                });
            }

            _cache.SetBlockedFiles(profile.Name, blockedForProfile);

            results.AddRange(blockedForProfile);
        }

        return results
            .OrderByDescending(file => file.SizeBytes)
            .ToList();
    }

    public Task<bool> UnblockAsync(
        string canonicalPath)
    {
        var success =
            ZoneIdentifierHelper.Unblock(canonicalPath);

        if (success)
        {
            _cache.RemoveFile(canonicalPath);
        }

        return Task.FromResult(success);
    }

    private async Task<List<DriveProfile>> ResolveProfilesAsync(
        string? driveName)
    {
        var enabledProfiles =
            await _settingsService.GetEnabledDriveProfilesAsync();

        return enabledProfiles
            .Where(profile =>
                string.IsNullOrWhiteSpace(driveName) ||
                string.Equals(profile.Name, driveName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private List<MediaAsset> GetEligibleAssets(
        DriveProfile profile)
    {
        return _mediaCatalogService.GetAssets()
            .Where(asset =>
                asset.MediaType is
                    MediaType.Movie or
                    MediaType.TvEpisode or
                    MediaType.Book or
                    MediaType.Audiobook or
                    MediaType.Music or
                    MediaType.Comic)
            .Where(asset =>
                PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.MovieLibraryPath) ||
                PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.TvLibraryPath) ||
                PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.BookLibraryPath) ||
                PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.AudiobookLibraryPath) ||
                PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.MusicLibraryPath) ||
                PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.ComicLibraryPath))
            .ToList();
    }
}
