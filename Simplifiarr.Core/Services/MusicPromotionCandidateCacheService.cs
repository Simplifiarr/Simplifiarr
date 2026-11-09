using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class MusicPromotionCandidateCacheService
    : IMusicPromotionCandidateCacheService
{
    private readonly DriveCandidateCache<MusicPromotionCandidate> _cache =
        new(
            "music-promotion-cache.json",
            candidate => candidate.SourcePath,
            candidate =>
                !string.IsNullOrWhiteSpace(candidate.SourcePath) &&
                File.Exists(candidate.SourcePath));

    public IReadOnlyList<MusicPromotionCandidate> GetCandidates(
        string driveName) =>
        _cache.GetCandidates(driveName);

    public DateTime? GetLastScanUtc(
        string driveName) =>
        _cache.GetLastScanUtc(driveName);

    public void SetCandidates(
        string driveName,
        IEnumerable<MusicPromotionCandidate> candidates) =>
        _cache.SetCandidates(driveName, candidates);

    public void RemoveCandidate(
        string sourcePath) =>
        _cache.RemoveCandidate(sourcePath);

    public void Clear(
        string? driveName = null) =>
        _cache.Clear(driveName);
}
