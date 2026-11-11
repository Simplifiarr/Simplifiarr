namespace Simplifiarr.Core.Models;

/// <summary>
/// Why a library file may not play.
/// </summary>
public enum PlaybackErrorKind
{
    /// <summary>Windows' "downloaded from the internet" mark.</summary>
    Blocked = 0,

    /// <summary>The account the media server runs as has no read access.</summary>
    MediaServerCantRead = 1,

    /// <summary>
    /// The file's permissions don't match its folder's - usually because it
    /// was moved in from somewhere else and kept that place's permissions.
    /// </summary>
    PermissionsDontMatchFolder = 2,

    /// <summary>Encrypted with Windows (EFS): only one Windows user can open it.</summary>
    Encrypted = 3,

    /// <summary>A cloud placeholder (OneDrive and similar): not really on the disk.</summary>
    CloudOnly = 4,

    /// <summary>The full path is longer than many programs can open.</summary>
    PathTooLong = 5,

    /// <summary>A name Windows programs struggle with: a trailing space or dot, or a reserved name.</summary>
    BadName = 6,

    /// <summary>A media file with nothing in it.</summary>
    Empty = 7,

    /// <summary>A download that never finished (.!qB, .part ...).</summary>
    Unfinished = 8,

    /// <summary>The release is still packed in an archive (.rar, .zip, .7z, .iso ...).</summary>
    Packed = 9
}

/// <summary>
/// One library file that may not play, and why.
/// </summary>
public class PlaybackError
{
    public PlaybackErrorKind Kind { get; set; }

    public string Path { get; set; } =
        string.Empty;

    public string FileName { get; set; } =
        string.Empty;

    public long SizeBytes { get; set; }

    public string DriveProfileName { get; set; } =
        string.Empty;

    /// <summary>
    /// A sentence about this file in particular, e.g. which account can't
    /// read it or how long the path is.
    /// </summary>
    public string Detail { get; set; } =
        string.Empty;

    /// <summary>For Blocked: where Windows thinks the file came from, if it says.</summary>
    public string? SourceUrl { get; set; }

    public DateTime DiscoveredUtc { get; set; }
}
