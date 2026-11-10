using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Where a running search has got to.
/// </summary>
public sealed record SizeReductionProgress(
    string DriveName,
    int Done,
    int Total,
    string CurrentTitle);

/// <summary>
/// Looks on the indexers (through Prowlarr) for smaller releases of films
/// and episodes already in the libraries. Report-only: nothing is ever
/// downloaded or replaced.
///
/// One search runs at a time for the whole app, and it keeps running if
/// the page that started it is closed - so the state lives here, not on
/// the page.
/// </summary>
public interface ISizeReductionCandidateService
{
    /// <summary>
    /// The resolutions that are searched ("2160P", "1440P", "1080P", "720P").
    /// </summary>
    IReadOnlyList<string> Tiers { get; }

    /// <summary>
    /// Releases saving less than this are left out everywhere - noise like
    /// samples and mislabeled releases isn't worth showing.
    /// </summary>
    long MinSavingsBytes { get; }

    bool IsSearching { get; }

    /// <summary>Null when no search is running.</summary>
    SizeReductionProgress? Progress { get; }

    /// <summary>
    /// How the last search since the app started ended, as a sentence.
    /// </summary>
    string? LastMessage { get; }

    bool LastSucceeded { get; }

    /// <summary>
    /// Raised (from the search's own thread) whenever any of the above
    /// changes.
    /// </summary>
    event Action? Changed;

    /// <summary>
    /// Searches one drive, or every enabled drive when no name is given.
    /// Does nothing if a search is already running. A drive's earlier
    /// results are only replaced when its search runs to the end - a
    /// stopped or failed search leaves them as they were.
    /// </summary>
    Task SearchAsync(
        string? driveName = null);

    void Stop();

    /// <summary>
    /// Everything found so far on the enabled drives.
    /// </summary>
    Task<List<SizeReductionCandidate>> GetFoundAsync();

    /// <summary>
    /// When this drive was last searched to the end, if ever.
    /// </summary>
    DateTime? GetLastSearchUtc(
        string driveName);

    /// <summary>
    /// Films and episodes whose resolution isn't known yet, so there is
    /// nothing to compare a release against.
    /// </summary>
    Task<int> CountUnscannedAssetsAsync(
        string? driveName = null);
}
