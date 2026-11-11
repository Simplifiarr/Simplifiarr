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

    public DefaultScanStepProvider(
        IMoviePromotionCandidateService movies,
        IMediaPromotionService mediaPromotion,
        IWasteCandidateService waste,
        IJunkFileScannerService junk,
        IPlaybackErrorService playbackErrors)
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

        _steps = steps;
    }

    public IReadOnlyList<ScanStep> GetSteps() => _steps;
}
