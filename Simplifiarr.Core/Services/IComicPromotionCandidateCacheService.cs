using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IComicPromotionCandidateCacheService
{
    IReadOnlyList<ComicPromotionCandidate> GetCandidates(
        string driveName);

    DateTime? GetLastScanUtc(
        string driveName);

    void SetCandidates(
        string driveName,
        IEnumerable<ComicPromotionCandidate> candidates);

    void RemoveCandidate(
        string sourcePath);

    void Clear(
        string? driveName = null);
}
