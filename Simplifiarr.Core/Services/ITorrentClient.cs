using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Everything Simplifiarr asks of a torrent client. Each supported client
/// (qBittorrent today) implements this; nothing else talks to the client.
/// </summary>
public interface ITorrentClient
{
    /// <summary>The client's name, for messages: "qBittorrent".</summary>
    string Name { get; }

    /// <summary>
    /// Where new torrents are saved, and each category's folder. Null when
    /// the client can't say (not supported, or unreachable).
    /// </summary>
    Task<TorrentClientLayout?> GetLayoutAsync() =>
        Task.FromResult<TorrentClientLayout?>(null);

    /// <summary>Sets where new torrents are saved. Null on success, otherwise why not.</summary>
    Task<string?> SetDefaultSavePathAsync(string path) =>
        Task.FromResult<string?>($"{Name} can't be changed from Simplifiarr.");

    TorrentClientCapabilities Capabilities { get; }

    Task<List<TorrentRecord>> GetTorrentsAsync();

    /// <summary>
    /// Like <see cref="GetTorrentsAsync"/> but returns null when the client
    /// can't be reached (instead of an empty list). Anything that deletes
    /// based on "this isn't in the client" must use this - an unreachable
    /// client must never look like an empty one.
    /// </summary>
    Task<List<TorrentRecord>?> TryGetTorrentsAsync();

    /// <summary>The torrent's .torrent file, or null if it can't be had.</summary>
    Task<byte[]?> GetTorrentExportBytesAsync(string torrentHash);

    /// <summary>
    /// Adds a torrent STOPPED, with checking NOT skipped - the user must
    /// recheck and start it themselves. Deliberately never starts it,
    /// unlike tools (e.g. cross-seed) that pause only briefly before
    /// starting unattended.
    /// </summary>
    Task<TorrentAddResult> AddTorrentAsync(byte[] torrentBytes, string savePath);

    /// <summary>
    /// Adds a torrent and starts downloading it straight away, with a tag
    /// so Simplifiarr can find it again. Only for a download you asked for.
    /// </summary>
    Task<TorrentAddResult> DownloadTorrentAsync(byte[] torrentBytes, string savePath, string tag);

    /// <summary>The same, from a magnet link.</summary>
    Task<TorrentAddResult> DownloadMagnetAsync(string magnet, string savePath, string tag);

    /// <summary>The torrent's files, or null if the client can't be reached.</summary>
    Task<List<TorrentContentFile>?> GetTorrentFilesAsync(string torrentHash);

    // The calls below change a torrent that is already in the client. Only
    // Move Seeds uses them, one torrent at a time and with the user's
    // confirmation. Each returns null on success, otherwise why it failed.

    Task<string?> StopTorrentAsync(string torrentHash);

    Task<string?> StartTorrentAsync(string torrentHash);

    /// <summary>
    /// Points the torrent at another folder. Files missing there are moved;
    /// see <see cref="TorrentClientCapabilities.RelocateKeepsExistingFiles"/>
    /// for what happens to files already there.
    /// </summary>
    Task<string?> SetLocationAsync(string torrentHash, string location);

    Task<string?> RecheckTorrentAsync(string torrentHash);

    /// <summary>
    /// Removes a torrent from the client, and its downloaded files with it
    /// when asked. Null when it worked, otherwise what went wrong.
    /// </summary>
    Task<string?> RemoveTorrentAsync(string torrentHash, bool deleteFiles);

    /// <summary>Adds a tag to a torrent. Null on success.</summary>
    Task<string?> AddTagAsync(string torrentHash, string tag);

    /// <summary>
    /// Tries the address and login given, saved or not. Null when it
    /// worked, otherwise what went wrong in words.
    /// </summary>
    Task<string?> TestConnectionAsync(string url, string username, string password);
}
