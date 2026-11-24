using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// The work a scan does for each drive, in order. Every step asks for a
/// fresh result, but because the scan has already read the drive's folders
/// once, none of them goes back to the disk for a file listing.
///
/// Size Reduction is deliberately not here: it searches indexers over the
/// network, so it stays on its own page as something you ask for.
/// </summary>
public class DefaultScanStepProvider
    : IScanStepProvider
{
    private readonly IReadOnlyList<ScanStep> _steps;
    private readonly IReadOnlyList<FinishingStep> _finishing;

    public DefaultScanStepProvider(
        IMoviePromotionCandidateService movies,
        IMediaPromotionService mediaPromotion,
        ICopiesService copies,
        IJunkFileScannerService junk,
        IPlaybackErrorService playbackErrors,
        ISeedMoveService seedMoves,
        IStorageService storage,
        IArrEnrichmentService arrEnrichment,
        IWatchHistoryService watchHistory,
        ISizeReductionDownloadService sizeReductionDownloads,
        IRollingShowService rollingShows,
        IRollingMovieService rollingMovies,
        IDownloadsDriveCheck downloadsDrive)
    {
        var steps = new List<ScanStep>
        {
            new(
                "Finding movies and TV not in your library",
                profile => movies.GetCandidatesAsync(profile.Name, forceRefresh: true, MediaKind.All))
        };

        // One step per media type the shared pipeline knows about - a new
        // type added there is scanned here without touching this file.
        foreach (var definition in MediaTypeDefinitions.All)
        {
            steps.Add(
                new ScanStep(
                    $"Finding {definition.PluralName.ToLowerInvariant()} not in your library",
                    profile => mediaPromotion.ScanAsync(definition.MediaType, profile.Name)));
        }

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

            // Every drive at once, now that each drive's folders are read:
            // what is on disk more than once, and which downloads the
            // library already has. Uses the listings just saved.
            new FinishingStep(
                "Matching downloads to your library",
                () => copies.ScanAsync()),

            // With a downloads drive: is everything new arriving there?
            new FinishingStep(
                "Checking where new downloads land",
                downloadsDrive.CheckAsync),

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
                () => rollingShows.RunAsync()),

            // Rolling Movies rules that are on, with the watch history just read.
            new FinishingStep(
                "Applying Rolling Movies rules",
                rollingMovies.RunAsync)
        ];
    }

    public IReadOnlyList<ScanStep> GetSteps() => _steps;

    public IReadOnlyList<FinishingStep> GetFinishingSteps() => _finishing;
}
