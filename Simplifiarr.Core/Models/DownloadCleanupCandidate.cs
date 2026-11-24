namespace Simplifiarr.Core.Models;

/// <summary>
/// How a download file was proven to be the same content as a library file.
/// </summary>
public enum DownloadMatchKind
{
    /// <summary>The download file and the library file are the same file on disk (a hardlink).</summary>
    Hardlink,

    /// <summary>A separate copy whose every byte matches the library file.</summary>
    IdenticalCopy,

    /// <summary>
    /// Not the same file: another release (often another quality) of a
    /// movie or episode the library already has. Goes to Quarantine, not
    /// deleted, since nothing proves it identical.
    /// </summary>
    OtherRelease,

    /// <summary>The library doesn't have it: shown, never removed.</summary>
    NotInLibrary
}

/// <summary>
/// A file in a download folder that also exists, verified, in a library and
/// is no longer part of any torrent in the client - so the download copy
/// can go.
/// </summary>
public class DownloadCleanupCandidate
{
    public string DownloadPath { get; set; } =
        string.Empty;

    public string FileName { get; set; } =
        string.Empty;

    public long SizeBytes { get; set; }

    public string LibraryPath { get; set; } =
        string.Empty;

    public DownloadMatchKind MatchKind { get; set; }

    public string DriveProfileName { get; set; } =
        string.Empty;

    /// <summary>
    /// Modified times at scan time - checked again before anything is
    /// removed, so a file edited after the scan is never touched.
    /// </summary>
    public DateTime DownloadModifiedUtc { get; set; }

    public DateTime LibraryModifiedUtc { get; set; }

    /// <summary>For another release: the library file's size.</summary>
    public long LibrarySizeBytes { get; set; }

    /// <summary>For another release: "720p" and "1080p", when the names say.</summary>
    public string DownloadQuality { get; set; } = string.Empty;

    public string LibraryQuality { get; set; } = string.Empty;
}
