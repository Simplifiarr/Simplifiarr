namespace Simplifiarr.Core.Models;

/// <summary>
/// One entry in a multi-file torrent's info.files list (BEP 3). All
/// entries are logically concatenated end-to-end, in this exact list
/// order, into one continuous virtual byte stream starting at offset 0 -
/// list order is semantically load-bearing, not cosmetic; it defines that
/// layout.
/// </summary>
public class TorrentFileEntry
{
    /// <summary>
    /// Path segments as declared by the torrent (e.g. ["Season 1", "S01E01.mkv"]),
    /// kept unjoined so a caller can rebuild an OS path at arbitrary nesting
    /// depth via Path.Combine rather than re-splitting a pre-joined string.
    /// </summary>
    public List<string> PathSegments { get; set; } =
        [];

    public long LengthBytes { get; set; }

    /// <summary>
    /// This entry's byte offset within the torrent's whole virtual stream -
    /// the sum of every strictly-preceding entry's LengthBytes. Precomputed
    /// once at parse time so verification/reconstruction never need to
    /// re-sum preceding entries.
    /// </summary>
    public long StreamOffsetBytes { get; set; }

    /// <summary>
    /// Display/logging convenience - PathSegments joined with the OS
    /// separator.
    /// </summary>
    public string RelativePath =>
        Path.Combine(PathSegments.ToArray());
}
