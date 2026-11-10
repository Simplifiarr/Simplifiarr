using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Report-only: finds smaller alternate releases of library files via
/// Prowlarr. A release is never downloaded to check it - these are
/// different encodes, so there is nothing byte-for-byte to prove. Everything
/// is judged from what is already known about the library file and from
/// the release's own listing (its stated size, and the resolution and codec
/// in its name).
/// </summary>
public class SizeReductionCandidateService
    : ISizeReductionCandidateService
{
    private static readonly string[] TierNames =
        ["2160P", "1440P", "1080P", "720P"];

    private static readonly Dictionary<string, int> ResolutionRank =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["2160P"] = 4,
            ["1440P"] = 3,
            ["1080P"] = 2,
            ["720P"] = 1,
            ["480P"] = 0
        };

    private const long MinSavingsBytesConst = 800L * 1024 * 1024;

    private readonly ISettingsService _settingsService;
    private readonly IMediaCatalogService _mediaCatalogService;
    private readonly IProwlarrClient _prowlarrClient;
    private readonly IReleaseParser _releaseParser;
    private readonly ISizeReductionCandidateCacheService _candidateCache;

    private readonly object _sync = new();

    private CancellationTokenSource? _cancellation;

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

    public IReadOnlyList<string> Tiers => TierNames;

    public long MinSavingsBytes => MinSavingsBytesConst;

    public bool IsSearching { get; private set; }

    public SizeReductionProgress? Progress { get; private set; }

    public string? LastMessage { get; private set; }

    public bool LastSucceeded { get; private set; }

    public event Action? Changed;

    public async Task SearchAsync(
        string? driveName = null)
    {
        CancellationToken token;

        lock (_sync)
        {
            if (IsSearching)
            {
                return;
            }

            IsSearching = true;

            _cancellation = new CancellationTokenSource();

            token = _cancellation.Token;
        }

        LastMessage = null;

        Changed?.Invoke();

        try
        {
            var profiles =
                await ResolveProfilesAsync(driveName);

            // Prowlarr answers a search it can't run with "nothing found",
            // which would look exactly like a clean result and wipe what
            // was found before. So make sure it is there first.
            if (!await _prowlarrClient.TestConnectionAsync())
            {
                Finish(
                    false,
                    "Couldn't reach Prowlarr. Check its address and API key in Settings, under Connections. Earlier results were kept.");

                return;
            }

            var filesWithSmaller = 0;

            foreach (var profile in profiles)
            {
                filesWithSmaller +=
                    await SearchDriveAsync(profile, token);
            }

            Finish(
                true,
                filesWithSmaller == 0
                    ? "Search finished. No smaller releases were found."
                    : $"Search finished. {filesWithSmaller} file(s) have a smaller release.");
        }
        catch (OperationCanceledException)
        {
            Finish(
                false,
                "Search stopped. Drives that weren't finished kept their earlier results.");
        }
        catch (Exception exception)
        {
            AppLog.Warn("Size reduction search failed.", exception);

            Finish(
                false,
                $"The search failed: {exception.Message} Earlier results were kept.");
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _cancellation?.Cancel();
        }
    }

    public async Task<List<SizeReductionCandidate>> GetFoundAsync()
    {
        var profiles =
            await ResolveProfilesAsync(null);

        return profiles
            .SelectMany(profile =>
                TierNames.SelectMany(tier =>
                    _candidateCache.GetCandidates(profile.Name, tier)))
            .Where(candidate => candidate.SavingsBytes >= MinSavingsBytes)
            .ToList();
    }

    public DateTime? GetLastSearchUtc(
        string driveName) =>
        TierNames
            .Select(tier => _candidateCache.GetLastScanUtc(driveName, tier))
            .Max();

    public async Task<int> CountUnscannedAssetsAsync(
        string? driveName = null)
    {
        var profiles =
            await ResolveProfilesAsync(driveName);

        return _mediaCatalogService.GetAssets().Count(asset =>
            IsVideo(asset) &&
            IsUnderAnyProfile(asset, profiles) &&
            IsUnknownResolution(asset.Resolution));
    }

    /// <summary>
    /// Searches every film and episode on one drive, then replaces that
    /// drive's results in one go. Returns how many files have a smaller
    /// release.
    /// </summary>
    private async Task<int> SearchDriveAsync(
        DriveProfile profile,
        CancellationToken token)
    {
        var assets =
            _mediaCatalogService.GetAssets()
                .Where(asset =>
                    IsVideo(asset) &&
                    IsUnderAnyProfile(asset, [profile]) &&
                    TierNames.Contains(asset.Resolution, StringComparer.OrdinalIgnoreCase))
                .OrderBy(asset => asset.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

        var found =
            new List<SizeReductionCandidate>();

        var searchesByQuery =
            new Dictionary<string, List<ProwlarrSearchResult>>(
                StringComparer.OrdinalIgnoreCase);

        var anyResults = false;

        for (var index = 0; index < assets.Count; index++)
        {
            token.ThrowIfCancellationRequested();

            var asset = assets[index];

            Progress =
                new SizeReductionProgress(
                    profile.Name,
                    index,
                    assets.Count,
                    DisplayTitle(asset));

            Changed?.Invoke();

            var query = BuildQuery(asset);
            var categoryId = GetCategoryId(asset.MediaType);
            var key = $"{categoryId}:{query}";

            if (!searchesByQuery.TryGetValue(key, out var releases))
            {
                releases =
                    await _prowlarrClient.SearchAsync(query, categoryId);

                searchesByQuery[key] = releases;
            }

            anyResults |= releases.Count > 0;

            found.AddRange(
                releases
                    .Select(release => Compare(asset, profile, release))
                    .OfType<SizeReductionCandidate>());
        }

        token.ThrowIfCancellationRequested();

        // Every single search coming back empty means Prowlarr stopped
        // answering part-way, not that the indexers have nothing at all.
        if (assets.Count > 0 && !anyResults)
        {
            throw new InvalidOperationException(
                $"Prowlarr returned nothing for any of the {assets.Count} file(s) on {profile.Name}.");
        }

        foreach (var tier in TierNames)
        {
            _candidateCache.SetCandidates(
                profile.Name,
                tier,
                found.Where(candidate =>
                    string.Equals(candidate.AssetResolution, tier, StringComparison.OrdinalIgnoreCase)));
        }

        return found
            .Select(candidate => candidate.MediaAssetId)
            .Distinct()
            .Count();
    }

    /// <summary>
    /// The release as a candidate for this file, or null when it isn't one:
    /// not smaller by enough, or plainly a different year's film.
    /// </summary>
    private SizeReductionCandidate? Compare(
        MediaAsset asset,
        DriveProfile profile,
        ProwlarrSearchResult release)
    {
        var savingsBytes =
            asset.FileSizeBytes - release.SizeBytes;

        // A release with no stated size would look like saving everything.
        if (release.SizeBytes <= 0 || savingsBytes < MinSavingsBytes)
        {
            return null;
        }

        var parsed =
            _releaseParser.Parse(release.Title);

        if (parsed.Year.HasValue &&
            asset.Year.HasValue &&
            parsed.Year != asset.Year)
        {
            return null;
        }

        var isSameResolution =
            !string.IsNullOrWhiteSpace(parsed.Resolution) &&
            string.Equals(
                parsed.Resolution,
                asset.Resolution,
                StringComparison.OrdinalIgnoreCase);

        var isUpgrade =
            !isSameResolution &&
            GetResolutionRank(parsed.Resolution) is int releaseRank &&
            GetResolutionRank(asset.Resolution) is int assetRank &&
            releaseRank > assetRank;

        return new SizeReductionCandidate
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
            Candidate = release,
            CandidateResolution = parsed.Resolution,
            CandidateCodec =
                string.IsNullOrWhiteSpace(parsed.Codec) ? null : parsed.Codec,
            IsSameResolution = isSameResolution,
            IsUpgrade = isUpgrade,
            SavingsBytes = savingsBytes,
            SavingsPercent = savingsBytes / (double)asset.FileSizeBytes,
            DiscoveredUtc = DateTime.UtcNow
        };
    }

    private void Finish(
        bool succeeded,
        string message)
    {
        lock (_sync)
        {
            IsSearching = false;

            _cancellation?.Dispose();
            _cancellation = null;
        }

        Progress = null;
        LastSucceeded = succeeded;
        LastMessage = message;

        Changed?.Invoke();
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

    private static bool IsVideo(
        MediaAsset asset) =>
        asset.MediaType is MediaType.Movie or MediaType.TvEpisode;

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

    private static string DisplayTitle(
        MediaAsset asset) =>
        asset.MediaType == MediaType.TvEpisode
            ? $"{asset.Title} S{asset.Season ?? 0:00}E{asset.Episode ?? 0:00}"
            : asset.Title;

    private static string BuildQuery(
        MediaAsset asset)
    {
        return asset.MediaType == MediaType.TvEpisode
            ? DisplayTitle(asset)
            : $"{asset.Title} {asset.Year}";
    }

    /// <summary>
    /// Torznab top-level category - keeps the search to video, so a film's
    /// title never brings back an album or a book of the same name (often
    /// tiny, which would look like a huge saving).
    /// </summary>
    private static int GetCategoryId(
        MediaType mediaType)
    {
        return mediaType == MediaType.TvEpisode ? 5000 : 2000;
    }
}
