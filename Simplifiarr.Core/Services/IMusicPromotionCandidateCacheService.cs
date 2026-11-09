using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IMusicPromotionCandidateCacheService
{
    IReadOnlyList<MusicPromotionCandidate> GetCandidates(
        string driveName);

    DateTime? GetLastScanUtc(
        string driveName);

    void SetCandidates(
        string driveName,
        IEnumerable<MusicPromotionCandidate> candidates);

    void RemoveCandidate(
        string sourcePath);

    void Clear(
        string? driveName = null);
}
