using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class WasteCandidateCacheService
    : IWasteCandidateCacheService
{
    private readonly DriveCandidateCache<WasteCandidate> _cache =
        new(
            "waste-cache.json",
            candidate => candidate.LibraryPath,
            candidate =>
                !string.IsNullOrWhiteSpace(candidate.LibraryPath) &&
                File.Exists(candidate.LibraryPath));

    public IReadOnlyList<WasteCandidate> GetCandidates(
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
        IEnumerable<WasteCandidate> candidates) =>
        _cache.SetCandidates(driveName, candidates);

    public int GetHealthyHardlinkCount(
        string driveName) =>
        _cache.GetHealthyHardlinkCount(driveName);

    public void SetHealthyHardlinkCount(
        string driveName,
        MediaKind kind,
        int count) =>
        _cache.SetHealthyHardlinkCount(driveName, kind, count);

    public void IncrementHealthyHardlinkCount(
        string driveName,
        MediaKind kind) =>
        _cache.IncrementHealthyHardlinkCount(driveName, kind);

    public void RemoveCandidate(
        string libraryPath) =>
        _cache.RemoveCandidate(libraryPath);

    public void Clear(
        string? driveName = null) =>
        _cache.Clear(driveName);
}
