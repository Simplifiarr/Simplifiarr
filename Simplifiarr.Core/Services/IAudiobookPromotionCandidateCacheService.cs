using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IAudiobookPromotionCandidateCacheService
{
    IReadOnlyList<AudiobookPromotionCandidate> GetCandidates(
        string driveName);

    DateTime? GetLastScanUtc(
        string driveName);

    void SetCandidates(
        string driveName,
        IEnumerable<AudiobookPromotionCandidate> candidates);

    void RemoveCandidate(
        string sourcePath);

    void Clear(
        string? driveName = null);
}
