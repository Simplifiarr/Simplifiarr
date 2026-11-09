using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IBookPromotionCandidateCacheService
{
    IReadOnlyList<BookPromotionCandidate> GetCandidates(
        string driveName);

    DateTime? GetLastScanUtc(
        string driveName);

    void SetCandidates(
        string driveName,
        IEnumerable<BookPromotionCandidate> candidates);

    void RemoveCandidate(
        string sourcePath);

    void Clear(
        string? driveName = null);
}
