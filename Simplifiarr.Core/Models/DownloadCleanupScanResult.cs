namespace Simplifiarr.Core.Models;

public class DownloadCleanupScanResult
{
    /// <summary>
    /// Set when the scan could not run safely (for example the client was
    /// unreachable) - nothing was scanned and the previous results are kept.
    /// </summary>
    public string? Error { get; set; }

    public List<DownloadCleanupCandidate> Candidates { get; set; } =
        [];

    public int TorrentCount { get; set; }

    public int DownloadFilesChecked { get; set; }

    /// <summary>
    /// Download files that have a same-size library file but were left
    /// alone because the client still has them.
    /// </summary>
    public int SkippedStillInClient { get; set; }
}
