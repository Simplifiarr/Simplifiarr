namespace Simplifiarr.Core.Models;

/// <summary>
/// One confirmed cross-seed candidate found by scanning an active torrent
/// against Prowlarr - already fully verified (Match.IsFullMatch is always
/// true for a result that made it into this list), just not yet committed
/// (hardlinked + saved) anywhere.
/// </summary>
public class CrossSeedScanResult
{
    /// <summary>
    /// The normalized title that was searched to find this candidate - not
    /// tied to one specific active torrent, since a candidate matched via
    /// the catalog fallback may have nothing to do with whichever torrent's
    /// search happened to surface it.
    /// </summary>
    public string SourceQueryTitle { get; set; } =
        string.Empty;

    public ProwlarrSearchResult CandidateResult { get; set; } =
        new();

    public TorrentMetadata CandidateMetadata { get; set; } =
        new();

    public byte[] CandidateTorrentBytes { get; set; } =
        [];

    public WholeTorrentMatchResult Match { get; set; } =
        new();

    /// <summary>
    /// Torrents already in qBittorrent whose data contains the matched
    /// files - i.e. what you're seeding these exact bytes as right now. The
    /// candidate is a different torrent (different info-hash) of the same
    /// files, which is what makes it a real cross-seed.
    /// </summary>
    public List<TorrentRecord> SeedingTorrents { get; set; } =
        [];

    /// <summary>
    /// The subset of <see cref="SeedingTorrents"/> already on one of this
    /// candidate's trackers. Adding the candidate would just seed the same
    /// files twice on the same tracker (a re-issued copy of a torrent you
    /// already have), not cross-seed to a new one.
    /// </summary>
    public List<TorrentRecord> SameTrackerTorrents { get; set; } =
        [];

    public bool IsAlreadySeededOnSameTracker =>
        SameTrackerTorrents.Count > 0;
}
