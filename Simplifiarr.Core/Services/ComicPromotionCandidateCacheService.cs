using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class ComicPromotionCandidateCacheService
    : IComicPromotionCandidateCacheService
{
    private readonly DriveCandidateCache<ComicPromotionCandidate> _cache =
        new(
            "comic-promotion-cache.json",
            candidate => candidate.SourcePath,
            candidate =>
                !string.IsNullOrWhiteSpace(candidate.SourcePath) &&
                File.Exists(candidate.SourcePath));

    public IReadOnlyList<ComicPromotionCandidate> GetCandidates(
        string driveName) =>
        _cache.GetCandidates(driveName);

    public DateTime? GetLastScanUtc(
        string driveName) =>
        _cache.GetLastScanUtc(driveName);

    public void SetCandidates(
        string driveName,
        IEnumerable<ComicPromotionCandidate> candidates) =>
        _cache.SetCandidates(driveName, candidates);

    public void RemoveCandidate(
        string sourcePath) =>
        _cache.RemoveCandidate(sourcePath);

    public void Clear(
        string? driveName = null) =>
        _cache.Clear(driveName);
}
