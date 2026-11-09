using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface ISizeReductionCandidateCacheService
{
    IReadOnlyList<SizeReductionCandidate> GetCandidates(
        string driveName,
        string resolutionTier);

    DateTime? GetLastScanUtc(
        string driveName,
        string resolutionTier);

    void SetCandidates(
        string driveName,
        string resolutionTier,
        IEnumerable<SizeReductionCandidate> candidates);
}
