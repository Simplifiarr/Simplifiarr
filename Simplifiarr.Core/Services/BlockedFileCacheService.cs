using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class BlockedFileCacheService
    : IBlockedFileCacheService
{
    private readonly DriveCandidateCache<BlockedFile> _cache =
        new(
            "blocked-files-cache.json",
            file => file.CanonicalPath,
            file => File.Exists(file.CanonicalPath));

    public IReadOnlyList<BlockedFile> GetBlockedFiles(
        string driveName) =>
        _cache.GetCandidates(driveName);

    public DateTime? GetLastScanUtc(
        string driveName) =>
        _cache.GetLastScanUtc(driveName);

    public void SetBlockedFiles(
        string driveName,
        IEnumerable<BlockedFile> files) =>
        _cache.SetCandidates(driveName, files);

    public void RemoveFile(
        string canonicalPath) =>
        _cache.RemoveCandidate(canonicalPath);
}
