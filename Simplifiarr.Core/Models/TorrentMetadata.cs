namespace Simplifiarr.Core.Models;

/// <summary>
/// Parsed from a .torrent file's "info" dictionary. PieceLengthBytes/Pieces
/// are exactly what BEP 3 defines for verifying data: each entry in Pieces
/// is the 20-byte SHA1 hash of one PieceLengthBytes-sized chunk of the
/// torrent's whole virtual byte stream (the last chunk is shorter when
/// TotalLengthBytes isn't evenly divisible by PieceLengthBytes). For a
/// multi-file torrent, that virtual stream is every Files entry
/// concatenated end-to-end in list order - TotalLengthBytes is the sum of
/// every entry's length either way.
/// </summary>
public class TorrentMetadata
{
    public string Name { get; set; } =
        string.Empty;

    public long PieceLengthBytes { get; set; }

    public List<byte[]> Pieces { get; set; } =
        [];

    public long TotalLengthBytes { get; set; }

    public bool IsSingleFile { get; set; }

    /// <summary>
    /// Empty for single-file torrents. For multi-file torrents, every file
    /// this torrent describes, in the exact order BEP 3's virtual stream
    /// concatenates them.
    /// </summary>
    public List<TorrentFileEntry> Files { get; set; } =
        [];

    /// <summary>
    /// BEP 3 info-hash (lowercase hex SHA1 of the raw "info" dict bytes) -
    /// the same identifier the client reports as a torrent's hash. Two
    /// torrents sharing this are the exact same content, regardless of
    /// which tracker each came from. Null if it couldn't be computed.
    /// </summary>
    public string? InfoHashHex { get; set; }

    /// <summary>
    /// Every tracker URL in the .torrent's "announce" / "announce-list"
    /// (outside the info dict, so not part of the info-hash).
    /// </summary>
    public List<string> TrackerUrls { get; set; } =
        [];
}
