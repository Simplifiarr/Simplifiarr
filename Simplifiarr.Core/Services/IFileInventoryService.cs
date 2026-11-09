using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IFileInventoryService
{
    Task<IReadOnlyList<FileRecord>> GetInventoryAsync(
        string folderPath,
        bool forceRefresh = false);

    DateTime? GetLastScanUtc(
        string folderPath);

    /// <summary>
    /// Starts a scan session. Until the returned object is disposed, each
    /// folder is read from disk at most once no matter how many features
    /// ask for a fresh listing of it, and a folder inside one that has
    /// already been read is answered from that reading. The cache file is
    /// written once, when the session ends.
    /// </summary>
    IDisposable BeginScanSession(
        CancellationToken cancellationToken = default,
        Action<long>? onFilesRead = null);
}
