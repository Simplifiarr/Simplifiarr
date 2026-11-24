using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Everything that takes space more than once, worked out in one pass over
/// every drive's library and download folders. Each download ends up in one
/// place: one file with a library name (fine), the same bytes as a library
/// file (link it or delete it), another version of a title the library has
/// (pick one), or not in the library at all (Hardlinks' job).
/// </summary>
public interface ICopiesService
{
    /// <summary>What the last check found, or null before the first one.</summary>
    CopiesScanResult? Last { get; }

    /// <summary>
    /// Checks again, using the file listings the last scan saved (folders
    /// whose listing is old or missing are read again). Asks the torrent
    /// client which downloads are still in use; nothing is read byte for
    /// byte here.
    /// </summary>
    Task<CopiesScanResult> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes each library copy another name for its download (the library
    /// copy goes to Quarantine first). Checked again right before.
    /// </summary>
    Task<CopiesActionResult> LinkAsync(IReadOnlyCollection<CopyToLink> links);

    /// <summary>
    /// Deletes download folders the library already has. Every copy is
    /// compared byte for byte with its library file first; anything that
    /// changed, or that a torrent uses again, is left alone.
    /// </summary>
    Task<CopiesActionResult> DeleteAsync(IReadOnlyCollection<DownloadLeftover> downloads);

    /// <summary>
    /// Moves one version of a title to Quarantine. Never the last one left,
    /// and never one a torrent is seeding.
    /// </summary>
    Task<CopiesActionResult> RemoveVersionAsync(CopyVersionGroup group, CopyFile version);

    /// <summary>Stops listing this file as a version of the title.</summary>
    void Ignore(CopyVersionGroup group, CopyFile version);
}
