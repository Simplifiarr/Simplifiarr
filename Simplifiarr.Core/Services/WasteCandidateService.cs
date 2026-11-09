using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class WasteCandidateService
    : IWasteCandidateService
{
    private readonly ISettingsService _settingsService;
    private readonly IDuplicateCandidateService _duplicateCandidateService;
    private readonly IHardlinkConversionPlanner _hardlinkConversionPlanner;
    private readonly IRemediationPreviewService _remediationPreviewService;
    private readonly IWasteCandidateCacheService _candidateCache;

    public WasteCandidateService(
        ISettingsService settingsService,
        IDuplicateCandidateService duplicateCandidateService,
        IHardlinkConversionPlanner hardlinkConversionPlanner,
        IRemediationPreviewService remediationPreviewService,
        IWasteCandidateCacheService candidateCache)
    {
        _settingsService = settingsService;
        _duplicateCandidateService = duplicateCandidateService;
        _hardlinkConversionPlanner = hardlinkConversionPlanner;
        _remediationPreviewService = remediationPreviewService;
        _candidateCache = candidateCache;
    }

    public async Task<List<WasteCandidate>> GetCandidatesAsync(
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
                    candidate => candidate.LibraryPath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(candidate => candidate.SizeBytes)
                .ToList();
        }

        var results =
            new List<WasteCandidate>();

        foreach (var profile in profiles)
        {
            var newProfileCandidates =
                new List<WasteCandidate>();

            if (!string.IsNullOrWhiteSpace(profile.DownloadPath) &&
                Directory.Exists(profile.DownloadPath))
            {
                foreach (var (libraryPath, kind) in GetLibraryPaths(profile, mediaKind))
                {
                    if (!Directory.Exists(libraryPath))
                    {
                        continue;
                    }

                    var rawCandidates =
                        await _duplicateCandidateService
                            .FindVideoCandidatesAsync(
                                libraryPath,
                                profile.DownloadPath,
                                forceRefresh: true);

                    var uniqueLibraryCandidates =
                        rawCandidates
                            .GroupBy(
                                candidate => candidate.LibraryPath,
                                StringComparer.OrdinalIgnoreCase)
                            .Select(group => group.First())
                            .ToList();

                    _candidateCache.SetHealthyHardlinkCount(
                        profile.Name,
                        kind,
                        uniqueLibraryCandidates.Count(candidate => candidate.IsAlreadyHardlinked));

                    var distinctCandidates =
                        uniqueLibraryCandidates
                            .Where(candidate => !candidate.IsAlreadyHardlinked)
                            .ToList();

                    foreach (var candidate in distinctCandidates)
                    {
                        var plan =
                            await _hardlinkConversionPlanner.CreatePlanAsync(
                                candidate);

                        if (plan is null)
                        {
                            continue;
                        }

                        var preview =
                            await _remediationPreviewService.CreatePreviewAsync(
                                plan);

                        newProfileCandidates.Add(
                            new WasteCandidate
                            {
                                LibraryPath = candidate.LibraryPath,
                                DownloadPath = candidate.DownloadPath,
                                LibraryFileName = candidate.LibraryFileName,
                                DownloadFileName = candidate.DownloadFileName,
                                SizeBytes = candidate.SizeBytes,
                                Kind = kind,
                                DriveProfileName = profile.Name,
                                DiscoveredUtc = DateTime.UtcNow,
                                Plan = plan,
                                CanConvert = preview.CanConvert,
                                Reason = preview.Reason
                            });
                    }
                }
            }

            // Only replace cache entries for the kind(s) just rescanned - otherwise
            // scanning Movies alone would wipe previously-cached TV waste candidates
            // for this profile (and vice versa).
            var existingCandidates =
                _candidateCache.GetCandidates(profile.Name);

            var preserved =
                mediaKind switch
                {
                    MediaKind.Movies => existingCandidates.Where(candidate => candidate.Kind == MediaKind.Tv),
                    MediaKind.Tv => existingCandidates.Where(candidate => candidate.Kind == MediaKind.Movies),
                    _ => Enumerable.Empty<WasteCandidate>()
                };

            var mergedCandidates =
                preserved
                    .Concat(newProfileCandidates)
                    .GroupBy(
                        candidate => candidate.LibraryPath,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

            _candidateCache.SetCandidates(
                profile.Name,
                mergedCandidates);

            _candidateCache.SetLastScanUtc(
                profile.Name,
                mediaKind);

            results.AddRange(newProfileCandidates);
        }

        return results
            .OrderByDescending(candidate => candidate.SizeBytes)
            .ToList();
    }

    private static bool MatchesKind(
        WasteCandidate candidate,
        MediaKind mediaKind)
    {
        return mediaKind == MediaKind.All || candidate.Kind == mediaKind;
    }

    private static IEnumerable<(string Path, MediaKind Kind)> GetLibraryPaths(
        DriveProfile profile,
        MediaKind mediaKind)
    {
        var paths =
            mediaKind switch
            {
                MediaKind.Movies =>
                    new[] { (profile.MovieLibraryPath, MediaKind.Movies) },
                MediaKind.Tv =>
                    new[] { (profile.TvLibraryPath, MediaKind.Tv) },
                _ =>
                    new[]
                    {
                        (profile.MovieLibraryPath, MediaKind.Movies),
                        (profile.TvLibraryPath, MediaKind.Tv)
                    }
            };

        return paths.Where(pair =>
            !string.IsNullOrWhiteSpace(pair.Item1));
    }
}
