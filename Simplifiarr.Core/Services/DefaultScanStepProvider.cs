using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// The work a scan does for each drive, in order. Every step asks for a
/// fresh result, but because the scan has already read the drive's folders
/// once, none of them goes back to the disk for a file listing.
///
/// Size Reduction and Download Cleanup are deliberately not here: the first
/// searches indexers over the network and the second compares whole files
/// byte for byte, so they stay on their own pages as things you ask for.
/// </summary>
public class DefaultScanStepProvider
    : IScanStepProvider
{
    private readonly IReadOnlyList<ScanStep> _steps;
    private readonly IReadOnlyList<FinishingStep> _finishing;

    public DefaultScanStepProvider(
        IMoviePromotionCandidateService movies,
        IMediaPromotionService mediaPromotion,
        IWasteCandidateService waste,
        IJunkFileScannerService junk,
        IPlaybackErrorService playbackErrors,
        ISeedMoveService seedMoves,
        IStorageService storage,
        IArrEnrichmentService arrEnrichment,
        IWatchHistoryService watchHistory,
        ISizeReductionDownloadService sizeReductionDownloads,
        IRollingShowService rollingShows)
    {
        var steps = new List<ScanStep>
        {
            new(
                "Finding movies and TV to link",
                profile => movies.GetCandidatesAsync(profile.Name, forceRefresh: true, MediaKind.All))
        };

        // One step per media type the shared pipeline knows about - a new
        // type added there is scanned here without touching this file.
        foreach (var definition in MediaTypeDefinitions.All)
        {
            steps.Add(
                new ScanStep(
                    $"Finding {definition.PluralName.ToLowerInvariant()} to link",
                    profile => mediaPromotion.ScanAsync(definition.MediaType, profile.Name)));
        }

        steps.Add(
            new ScanStep(
                "Checking for full copies",
                profile => waste.GetCandidatesAsync(profile.Name, forceRefresh: true, MediaKind.All)));

        steps.Add(
            new ScanStep(
                "Looking for leftover files",
                profile => junk.ScanAsync(profile.Name)));

        steps.Add(
            new ScanStep(
                "Checking for playback problems",
                profile => playbackErrors.ScanAsync(profile.Name)));

        // Sizes only; nothing is read or changed.
        steps.Add(
            new ScanStep(
                "Checking which drive each torrent seeds from",
                profile => seedMoves.ScanAsync(profile.Name)));

        steps.Add(
            new ScanStep(
                "Measuring library folders",
                profile => storage.ScanAsync(profile.Name)));

        _steps = steps;

        // Library files get Sonarr's and Radarr's titles, so Duplicates,
        // Collections and Hardlinks all use the names they use. Does
        // nothing when they aren't set up.
        _finishing =
        [
            new FinishingStep(
                "Taking titles from Sonarr and Radarr",
                async () =>
                {
                    await arrEnrichment.EnrichFromSonarrAsync();
                    await arrEnrichment.EnrichFromRadarrAsync();
                }),

            // Who has watched what, and who asked for it. Does nothing
            // when Jellyfin isn't set up.
            new FinishingStep(
                "Reading watch history from Jellyfin and Seerr",
                watchHistory.RefreshAsync),

            // Smaller releases you chose to download: finished ones are
            // swapped in when that is switched on, or wait for you.
            new FinishingStep(
                "Checking smaller releases being downloaded",
                sizeReductionDownloads.CheckAsync),

            // Rolling Shows rules that are on, in case Sonarr's webhook was
            // missed, and removing torrents that have seeded enough.
            new FinishingStep(
                "Applying Rolling Shows rules",
                () => rollingShows.RunAsync())
        ];
    }

    public IReadOnlyList<ScanStep> GetSteps() => _steps;

    public IReadOnlyList<FinishingStep> GetFinishingSteps() => _finishing;
}
