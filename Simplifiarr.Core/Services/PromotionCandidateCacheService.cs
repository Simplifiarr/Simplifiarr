using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class PromotionCandidateCacheService
    : IPromotionCandidateCacheService
{
    private readonly DriveCandidateCache<MoviePromotionCandidate> _cache =
        new(
            "promotion-cache.json",
            candidate => candidate.SourcePath,
            candidate =>
                !string.IsNullOrWhiteSpace(candidate.SourcePath) &&
                File.Exists(candidate.SourcePath));

    public IReadOnlyList<MoviePromotionCandidate> GetCandidates(
        string driveName) =>
        _cache.GetCandidates(driveName);

    public DateTime? GetLastScanUtc(
        string driveName) =>
        _cache.GetLastScanUtc(driveName);

    public DateTime? GetLastScanUtc(
        string driveName,
        MediaKind kind) =>
        _cache.GetLastScanUtc(driveName, kind);

    public void SetLastScanUtc(
        string driveName,
        MediaKind kind) =>
        _cache.SetLastScanUtc(driveName, kind);

    public void SetCandidates(
        string driveName,
        IEnumerable<MoviePromotionCandidate> candidates) =>
        _cache.SetCandidates(driveName, candidates);

    public void RemoveCandidate(
        string sourcePath) =>
        _cache.RemoveCandidate(sourcePath);

    public void Clear(
        string? driveName = null) =>
        _cache.Clear(driveName);
}
