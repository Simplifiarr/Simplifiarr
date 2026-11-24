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

    /// <summary>Last scan's leftovers, one per torrent folder (or loose file).</summary>
    IReadOnlyList<DownloadLeftover> GetLeftovers(
        string driveName);

    /// <summary>
    /// Removes whole leftovers: their media (deleted when the library has
    /// the same bytes, to Quarantine when it's another release), their
    /// junk, and the folder once nothing else is in it. Everything is
    /// checked again first; a leftover that no longer passes is skipped
    /// whole. Ones the library doesn't have are never removed.
    /// </summary>
    Task<DownloadCleanupRemoveResult> RemoveLeftoversAsync(
        IReadOnlyCollection<DownloadLeftover> leftovers);
}
