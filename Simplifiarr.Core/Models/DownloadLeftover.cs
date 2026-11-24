namespace Simplifiarr.Core.Models;

/// <summary>What the library has of a leftover download, from best to worst proof.</summary>
public enum DownloadLeftoverKind
{
    /// <summary>The library has the very same files (hardlinks): removing frees no space, it just tidies.</summary>
    SameFile,

    /// <summary>The library has byte-identical copies.</summary>
    IdenticalCopy,

    /// <summary>Nothing playable is left, only subtitles, pictures, .nfo files, unfinished parts and the like.</summary>
    LeftoversOnly,

    /// <summary>The library has the same movie or episode as a different file (often another quality).</summary>
    OtherRelease,

    /// <summary>The library doesn't have it at all: never imported, or removed since. Shown, never removed.</summary>
    NotInLibrary
}

/// <summary>
/// One torrent's worth of download that no torrent in the client uses any
/// more: its folder (or a lone file), the media in it and what the library
/// has of each, and the leftovers that go with it.
/// </summary>
public sealed class DownloadLeftover
{
    /// <summary>The torrent's folder, or the file itself when it sits loose in a downloads folder.</summary>
    public string Path { get; set; } = string.Empty;

    public bool IsFolder { get; set; }

    public string DriveProfileName { get; set; } = string.Empty;

    public DownloadLeftoverKind Kind { get; set; }

    /// <summary>The videos, music and books in it, each with what the library has of it.</summary>
    public List<DownloadCleanupCandidate> Media { get; set; } = [];

    /// <summary>Subtitles, pictures, .nfo, samples, unfinished parts: removed along with it.</summary>
    public List<string> Junk { get; set; } = [];

    public long JunkBytes { get; set; }

    /// <summary>
    /// Files Simplifiarr doesn't recognise (a game, an archive...). They're
    /// left alone, and so is the folder.
    /// </summary>
    public List<string> Kept { get; set; } = [];

    public long SizeBytes { get; set; }

    /// <summary>Space removing it gives back (hardlinked media frees nothing).</summary>
    public long FreedBytes { get; set; }

    /// <summary>Whether the folder itself goes once its files have.</summary>
    public bool RemovesFolder => IsFolder && Kept.Count == 0;

    public string Name => System.IO.Path.GetFileName(Path);
}
