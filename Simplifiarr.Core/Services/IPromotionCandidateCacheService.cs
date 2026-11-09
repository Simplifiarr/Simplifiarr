using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IPromotionCandidateCacheService
{
    IReadOnlyList<MoviePromotionCandidate> GetCandidates(
        string driveName);

    DateTime? GetLastScanUtc(
        string driveName);

    DateTime? GetLastScanUtc(
        string driveName,
        MediaKind kind);

    void SetLastScanUtc(
        string driveName,
        MediaKind kind);

    void SetCandidates(
        string driveName,
        IEnumerable<MoviePromotionCandidate> candidates);

    void RemoveCandidate(
        string sourcePath);

    void Clear(
        string? driveName = null);
}
