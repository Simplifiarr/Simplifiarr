using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface ISizeReductionCandidateService
{
    /// <summary>
    /// Candidates saving less than this are filtered out everywhere -
    /// noise like fansubs/samples/mislabeled releases isn't worth showing.
    /// </summary>
    long MinSavingsBytes { get; }

    /// <summary>
    /// resolutionTier must be one of the known ffprobe-bucketed values
    /// ("2160P", "1440P", "1080P", "720P") - each tier is scanned and
    /// cached independently. Report-only: never downloads a candidate,
    /// only searches Prowlarr and compares declared metadata.
    /// </summary>
    Task<List<SizeReductionCandidate>> GetCandidatesAsync(
        string resolutionTier,
        string? driveName = null,
        bool forceRefresh = false);

    /// <summary>
    /// Counts movie/TV assets under the resolved drive profile(s) whose
    /// Resolution is empty/"UNKNOWN" - i.e. never successfully ffprobed, so
    /// this feature has nothing trustworthy to compare against for them.
    /// </summary>
    Task<int> CountUnscannedAssetsAsync(
        string? driveName = null);
}
