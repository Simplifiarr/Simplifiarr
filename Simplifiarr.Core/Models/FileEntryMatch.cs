namespace Simplifiarr.Core.Models;

/// <summary>
/// One file entry of a candidate torrent that's been proven byte-identical
/// to a real file somewhere on disk. FileEntry is null when the candidate
/// is a single-file torrent - the whole torrent is the "entry" in that case.
/// </summary>
public class FileEntryMatch
{
    public TorrentFileEntry? FileEntry { get; set; }

    public string ResolvedPath { get; set; } =
        string.Empty;

    public PieceVerificationResult VerificationResult { get; set; } =
        new();
}
