namespace Simplifiarr.Core.Models;

/// <summary>
/// One file of a torrent as the client lists it: its path relative to the
/// torrent's save folder (including the torrent's own folder, if it has
/// one) and its size.
/// </summary>
public sealed record TorrentContentFile(
    string RelativePath,
    long SizeBytes);
