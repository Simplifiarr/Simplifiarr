namespace Simplifiarr.Core.Models;

/// <summary>
/// What a torrent is doing, in the same words for every torrent client.
/// Each client maps its own states onto these.
/// </summary>
public enum TorrentStatus
{
    Unknown,

    /// <summary>Fetching data.</summary>
    Downloading,

    /// <summary>Complete and sharing.</summary>
    Seeding,

    /// <summary>Paused or stopped, finished or not.</summary>
    Stopped,

    /// <summary>Waiting its turn in the client's queue.</summary>
    Queued,

    /// <summary>Getting ready: fetching metadata or allocating disk space.</summary>
    Preparing,

    /// <summary>Verifying the data on disk.</summary>
    Checking,

    /// <summary>The client is moving its files to another folder.</summary>
    Moving,

    /// <summary>The client reports a problem, such as missing files.</summary>
    Error
}

/// <summary>Which torrent client Simplifiarr talks to.</summary>
public enum TorrentClientKind
{
    QBittorrent
}

/// <summary>
/// What a torrent client can do through its API. Features that need
/// something the client can't do explain that instead of failing.
/// </summary>
public sealed record TorrentClientCapabilities
{
    /// <summary>Hand over a torrent's .torrent file (Cross-Seed needs this).</summary>
    public bool CanExportTorrentFiles { get; init; }

    /// <summary>Add a torrent paused, with checking (Cross-Seed, Torrent Rebuilder).</summary>
    public bool CanAddTorrents { get; init; }

    /// <summary>Pause, point at another folder and recheck a torrent (Move Seeds).</summary>
    public bool CanRelocate { get; init; }

    /// <summary>
    /// Pointing a torrent at another folder keeps files already there
    /// rather than overwriting them, so Move Seeds can link the library's
    /// files into place first and nothing is copied between drives.
    /// </summary>
    public bool RelocateKeepsExistingFiles { get; init; }

    /// <summary>The client groups torrents by category or label.</summary>
    public bool HasCategories { get; init; }
}
