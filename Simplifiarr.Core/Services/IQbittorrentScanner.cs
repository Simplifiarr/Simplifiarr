using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IQbittorrentScanner
{
    Task<List<TorrentRecord>> GetTorrentsAsync();

    /// <summary>
    /// Like <see cref="GetTorrentsAsync"/> but returns null when
    /// qBittorrent can't be reached (instead of an empty list). Anything
    /// that deletes based on "this isn't in qBittorrent" must use this -
    /// an unreachable client must never look like an empty one.
    /// </summary>
    Task<List<TorrentRecord>?> TryGetTorrentsAsync();

    Task<byte[]?> GetTorrentExportBytesAsync(string torrentHash);

    /// <summary>
    /// Adds a torrent to qBittorrent in a STOPPED/PAUSED state with
    /// checking NOT skipped - the user must manually force-recheck and
    /// start it themselves. Deliberately never auto-resumes, unlike tools
    /// (e.g. cross-seed) that pause only transiently before resuming
    /// unattended.
    /// </summary>
    Task<QbittorrentAddResult> AddTorrentAsync(byte[] torrentBytes, string savePath);

    /// <summary>The torrent's files, or null if qBittorrent can't be reached.</summary>
    Task<List<QbittorrentFile>?> GetTorrentFilesAsync(string torrentHash);

    // The calls below change a torrent that is already in qBittorrent. Only
    // Move Seeds uses them, one torrent at a time and with the user's
    // confirmation. Each returns null on success, otherwise why it failed.

    Task<string?> StopTorrentAsync(string torrentHash);

    Task<string?> StartTorrentAsync(string torrentHash);

    /// <summary>
    /// Points the torrent at another folder. qBittorrent moves any files
    /// that aren't already there and keeps the ones that are.
    /// </summary>
    Task<string?> SetLocationAsync(string torrentHash, string location);

    Task<string?> RecheckTorrentAsync(string torrentHash);
}
