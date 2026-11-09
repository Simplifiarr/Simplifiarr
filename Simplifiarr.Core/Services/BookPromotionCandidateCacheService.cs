using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class BookPromotionCandidateCacheService
    : IBookPromotionCandidateCacheService
{
    private readonly DriveCandidateCache<BookPromotionCandidate> _cache =
        new(
            "book-promotion-cache.json",
            candidate => candidate.SourcePath,
            candidate =>
                !string.IsNullOrWhiteSpace(candidate.SourcePath) &&
                File.Exists(candidate.SourcePath));

    public IReadOnlyList<BookPromotionCandidate> GetCandidates(
        string driveName) =>
        _cache.GetCandidates(driveName);

    public DateTime? GetLastScanUtc(
        string driveName) =>
        _cache.GetLastScanUtc(driveName);

    public void SetCandidates(
        string driveName,
        IEnumerable<BookPromotionCandidate> candidates) =>
        _cache.SetCandidates(driveName, candidates);

    public void RemoveCandidate(
        string sourcePath) =>
        _cache.RemoveCandidate(sourcePath);

    public void Clear(
        string? driveName = null) =>
        _cache.Clear(driveName);
}
