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
        IBookPromotionCandidateService books,
        IAudiobookPromotionCandidateService audiobooks,
        IMusicPromotionCandidateService music,
        IComicPromotionCandidateService comics,
        IWasteCandidateService waste,
        IJunkFileScannerService junk,
        IBlockedFileService blockedFiles)
    {
        _steps =
        [
            new ScanStep(
                "Finding movies and TV to link",
                profile => movies.GetCandidatesAsync(profile.Name, forceRefresh: true, MediaKind.All)),

            new ScanStep(
                "Finding books to link",
                profile => books.GetCandidatesAsync(profile.Name, forceRefresh: true)),

            new ScanStep(
                "Finding audiobooks to link",
                profile => audiobooks.GetCandidatesAsync(profile.Name, forceRefresh: true)),

            new ScanStep(
                "Finding music to link",
                profile => music.GetCandidatesAsync(profile.Name, forceRefresh: true)),

            new ScanStep(
                "Finding comics to link",
                profile => comics.GetCandidatesAsync(profile.Name, forceRefresh: true)),

            new ScanStep(
                "Checking for full copies",
                profile => waste.GetCandidatesAsync(profile.Name, forceRefresh: true, MediaKind.All)),

            new ScanStep(
                "Looking for leftover files",
                profile => junk.ScanAsync(profile.Name)),

            new ScanStep(
                "Checking for blocked files",
                profile => blockedFiles.GetBlockedFilesAsync(profile.Name, forceRefresh: true))
        ];
    }

    public IReadOnlyList<ScanStep> GetSteps() => _steps;
}
