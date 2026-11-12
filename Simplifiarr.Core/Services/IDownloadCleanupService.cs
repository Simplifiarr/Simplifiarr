using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Finds download-folder files that are also in a library (as the very same
/// file or a byte-identical copy) and that the client no longer has - so
/// the leftover download copy can be removed.
/// </summary>
public interface IDownloadCleanupService
{
    IReadOnlyList<DownloadCleanupCandidate> GetCandidates(
        string driveName);

    DateTime? GetLastScanUtc(
        string driveName);

    Task<DownloadCleanupScanResult> ScanAsync(
        string driveName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes each candidate's download copy directly (no Quarantine - a
    /// cross-drive move would turn a hardlink into a full copy), re-checking
    /// everything first (the client, the files themselves, the library
    /// copy) - a candidate that no longer passes is skipped, never removed.
    /// </summary>
    Task<DownloadCleanupRemoveResult> RemoveAsync(
        IReadOnlyCollection<DownloadCleanupCandidate> candidates);
}
