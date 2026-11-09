using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IWasteCandidateCacheService
{
    IReadOnlyList<WasteCandidate> GetCandidates(
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
        IEnumerable<WasteCandidate> candidates);

    int GetHealthyHardlinkCount(
        string driveName);

    void SetHealthyHardlinkCount(
        string driveName,
        MediaKind kind,
        int count);

    void IncrementHealthyHardlinkCount(
        string driveName,
        MediaKind kind);

    void RemoveCandidate(
        string libraryPath);

    void Clear(
        string? driveName = null);
}
