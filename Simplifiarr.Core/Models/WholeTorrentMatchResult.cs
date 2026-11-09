namespace Simplifiarr.Core.Models;

/// <summary>
/// Result of checking every file entry in a candidate torrent against a
/// source of real files - IsFullMatch requires every entry to resolve to a
/// verified byte-identical file. No partial credit: a candidate missing a
/// verified match for even one entry is not a match at all.
/// </summary>
public class WholeTorrentMatchResult
{
    public bool IsFullMatch { get; set; }

    public List<FileEntryMatch> EntryMatches { get; set; } =
        [];

    public string? FailureReason { get; set; }

    /// <summary>
    /// The whole-torrent proof behind a full match: every piece of the
    /// torrent (boundary pieces included) hashed against the matched files.
    /// Null when the candidate wasn't a full match.
    /// </summary>
    public PieceVerificationResult? WholeTorrentVerification { get; set; }
}
