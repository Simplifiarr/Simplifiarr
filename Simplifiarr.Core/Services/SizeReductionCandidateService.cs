using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Report-only: finds smaller alternate releases of library files via
/// Prowlarr. Never downloads a candidate to verify it (unlike Cross-Seed,
/// there is no byte-exact proof possible here - these are genuinely
/// different encodes) - everything is judged from the asset's own already-
/// known metadata and the candidate's cheap Prowlarr search-result metadata
/// (declared size, release-title-parsed resolution/codec via
/// IReleaseParser). Same-resolution candidates are preferred but not
/// required - a smaller candidate at a different resolution still shows,
/// clearly flagged, sorted after same-resolution matches.
/// </summary>
public class SizeReductionCandidateService
    : ISizeReductionCandidateService
{
    private readonly ISettingsService _settingsService;
    private readonly IMediaCatalogService _mediaCatalogService;
    private readonly IProwlarrClient _prowlarrClient;
    private readonly IReleaseParser _releaseParser;
    private readonly ISizeReductionCandidateCacheService _candidateCache;

    private static readonly Dictionary<string, int> ResolutionRank =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["2160P"] = 4,
            ["1440P"] = 3,
            ["1080P"] = 2,
            ["720P"] = 1,
            ["480P"] = 0
        };

    /// <summary>
    /// Candidates saving less than this are noise (fansubs, samples,
    /// mislabeled releases) - filtered out rather than shown, per the
    /// user's explicit "anything under 800MB isn't worth considering" call.
    /// </summary>
    private const long MinSavingsBytesConst = 800L * 1024 * 1024;

    public long MinSavingsBytes => MinSavingsBytesConst;

    public SizeReductionCandidateService(
        ISettingsService settingsService,
        IMediaCatalogService mediaCatalogService,
        IProwlarrClient prowlarrClient,
        IReleaseParser releaseParser,
        ISizeReductionCandidateCacheService candidateCache)
    {
        _settingsService = settingsService;
        _mediaCatalogService = mediaCatalogService;
        _prowlarrClient = prowlarrClient;
        _releaseParser = releaseParser;
        _candidateCache = candidateCache;
    }

    public async Task<List<SizeReductionCandidate>> GetCandidatesAsync(
        string resolutionTier,
        string? driveName = null,
        bool forceRefresh = false)
    {
        var profiles =
            await ResolveProfilesAsync(driveName);

        if (!forceRefresh)
        {
            return profiles
                .SelectMany(profile =>
                    _candidateCache.GetCandidates(profile.Name, resolutionTier))
                .Where(candidate => candidate.SavingsBytes >= MinSavingsBytes)
                .OrderByDescending(candidate => candidate.IsUpgrade)
                .ThenByDescending(candidate => candidate.IsSameResolution)
                .ThenByDescending(candidate => candidate.SavingsBytes)
                .ToList();
        }

        var results =
            new List<SizeReductionCandidate>();

        var queryResultCache =
            new Dictionary<string, List<ProwlarrSearchResult>>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            var assets =
                GetEligibleAssets(profile, resolutionTier);

            var profileCandidates =
                new List<SizeReductionCandidate>();

            foreach (var asset in assets)
            {
                var query =
                    BuildQuery(asset);

                var categoryId =
                    GetCategoryId(asset.MediaType);

                var cacheKey =
                    $"{categoryId}:{query}";

                if (!queryResultCache.TryGetValue(cacheKey, out var searchResults))
                {
                    searchResults =
                        await _prowlarrClient.SearchAsync(query, categoryId);

                    queryResultCache[cacheKey] = searchResults;
                }

                foreach (var result in searchResults)
                {
                    if (result.SizeBytes >= asset.FileSizeBytes)
                    {
                        continue;
                    }

                    var parsed =
                        _releaseParser.Parse(result.Title);

                    if (parsed.Year.HasValue &&
                        asset.Year.HasValue &&
                        parsed.Year != asset.Year)
                    {
                        continue;
                    }

                    var isSameResolution =
                        !string.IsNullOrWhiteSpace(parsed.Resolution) &&
                        string.Equals(
                            parsed.Resolution,
                            asset.Resolution,
                            StringComparison.OrdinalIgnoreCase);

                    var isUpgrade =
                        !isSameResolution &&
                        GetResolutionRank(parsed.Resolution) is int candidateRank &&
                        GetResolutionRank(asset.Resolution) is int assetRank &&
                        candidateRank > assetRank;

                    var savingsBytes =
                        asset.FileSizeBytes - result.SizeBytes;

                    if (savingsBytes < MinSavingsBytes)
                    {
                        continue;
                    }

                    profileCandidates.Add(new SizeReductionCandidate
                    {
                        MediaAssetId = asset.Id,
                        AssetTitle = asset.Title,
                        AssetYear = asset.Year,
                        Season = asset.Season,
                        Episode = asset.Episode,
                        CanonicalPath = asset.CanonicalPath,
                        AssetSizeBytes = asset.FileSizeBytes,
                        AssetResolution = asset.Resolution,
                        AssetVideoCodec = asset.VideoCodec,
                        DriveProfileName = profile.Name,
                        Candidate = result,
                        CandidateResolution = parsed.Resolution,
                        CandidateCodec =
                            string.IsNullOrWhiteSpace(parsed.Codec) ? null : parsed.Codec,
                        IsSameResolution = isSameResolution,
                        IsUpgrade = isUpgrade,
                        SavingsBytes = savingsBytes,
                        SavingsPercent = savingsBytes / (double)asset.FileSizeBytes,
                        DiscoveredUtc = DateTime.UtcNow
                    });
                }
            }

            var sortedProfileCandidates =
                profileCandidates
                    .OrderByDescending(candidate => candidate.IsSameResolution)
                    .ThenByDescending(candidate => candidate.SavingsBytes)
                    .ToList();

            _candidateCache.SetCandidates(
                profile.Name,
                resolutionTier,
                sortedProfileCandidates);

            results.AddRange(sortedProfileCandidates);
        }

        return results
            .OrderByDescending(candidate => candidate.IsSameResolution)
            .ThenByDescending(candidate => candidate.SavingsBytes)
            .ToList();
    }

    public async Task<int> CountUnscannedAssetsAsync(
        string? driveName = null)
    {
        var profiles =
            await ResolveProfilesAsync(driveName);

        var assets =
            _mediaCatalogService.GetAssets();

        return assets.Count(asset =>
            (asset.MediaType == MediaType.Movie || asset.MediaType == MediaType.TvEpisode) &&
            IsUnderAnyProfile(asset, profiles) &&
            IsUnknownResolution(asset.Resolution));
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
        DriveProfile profile,
        string resolutionTier)
    {
        return _mediaCatalogService.GetAssets()
            .Where(asset =>
                (asset.MediaType == MediaType.Movie || asset.MediaType == MediaType.TvEpisode) &&
                IsUnderAnyProfile(asset, [profile]) &&
                string.Equals(asset.Resolution, resolutionTier, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool IsUnderAnyProfile(
        MediaAsset asset,
        IReadOnlyCollection<DriveProfile> profiles)
    {
        return profiles.Any(profile =>
            PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.MovieLibraryPath) ||
            PathContainment.IsPathUnderRoot(asset.CanonicalPath, profile.TvLibraryPath));
    }

    private static bool IsUnknownResolution(
        string resolution)
    {
        return string.IsNullOrWhiteSpace(resolution) ||
               string.Equals(resolution, "UNKNOWN", StringComparison.OrdinalIgnoreCase);
    }

    private static int? GetResolutionRank(
        string resolution)
    {
        return ResolutionRank.TryGetValue(resolution, out var rank) ? rank : null;
    }

    private static string BuildQuery(
        MediaAsset asset)
    {
        return asset.MediaType == MediaType.TvEpisode
            ? $"{asset.Title} S{asset.Season ?? 0:00}E{asset.Episode ?? 0:00}"
            : $"{asset.Title} {asset.Year}";
    }

    /// <summary>
    /// Torznab top-level category - scopes the Prowlarr search itself to
    /// video content, so a query like "Weapons 2025" never surfaces an
    /// unrelated music/book/etc. release that happens to share the title
    /// (and is often tiny, which would otherwise look like a huge "savings"
    /// candidate purely from matching text, not actually being the movie).
    /// </summary>
    private static int GetCategoryId(
        MediaType mediaType)
    {
        return mediaType == MediaType.TvEpisode ? 5000 : 2000;
    }
}
