namespace Simplifiarr.Core.Models;

/// <summary>
/// One file of a torrent as qBittorrent lists it: its path relative to the
/// torrent's save folder (including the torrent's own folder, if it has
/// one) and its size.
/// </summary>
public sealed record QbittorrentFile(
    string RelativePath,
    long SizeBytes);
