using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IBlockedFileCacheService
{
    IReadOnlyList<BlockedFile> GetBlockedFiles(
        string driveName);

    DateTime? GetLastScanUtc(
        string driveName);

    void SetBlockedFiles(
        string driveName,
        IEnumerable<BlockedFile> files);

    void RemoveFile(
        string canonicalPath);
}
