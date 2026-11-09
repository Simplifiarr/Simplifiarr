using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Reuses the shared DriveCandidateCache&lt;T&gt; JSON-persistence machinery
/// (same pattern as WasteCandidateCacheService) keyed by a synthesized
/// "driveName::resolutionTier" compound key, so scanning one resolution
/// tier never disturbs another tier's cached results for the same drive -
/// each tier is just a distinct cache entry, no special merge logic needed.
/// </summary>
public class SizeReductionCandidateCacheService
    : ISizeReductionCandidateCacheService
{
    private readonly DriveCandidateCache<SizeReductionCandidate> _cache =
        new(
            "size-reduction-cache.json",
            candidate => candidate.CanonicalPath + "|" + candidate.Candidate.DownloadUrl,
            candidate =>
                !string.IsNullOrWhiteSpace(candidate.CanonicalPath) &&
                File.Exists(candidate.CanonicalPath));

    public IReadOnlyList<SizeReductionCandidate> GetCandidates(
        string driveName,
        string resolutionTier) =>
        _cache.GetCandidates(BuildKey(driveName, resolutionTier));

    public DateTime? GetLastScanUtc(
        string driveName,
        string resolutionTier) =>
        _cache.GetLastScanUtc(BuildKey(driveName, resolutionTier));

    public void SetCandidates(
        string driveName,
        string resolutionTier,
        IEnumerable<SizeReductionCandidate> candidates) =>
        _cache.SetCandidates(BuildKey(driveName, resolutionTier), candidates);

    private static string BuildKey(
        string driveName,
        string resolutionTier) =>
        $"{driveName}::{resolutionTier}";
}
