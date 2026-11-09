using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IWholeTorrentCandidateService
{
    /// <summary>
    /// Read-only. For every non-empty file entry in candidateTorrent (or the
    /// whole torrent, if single-file), searches primarySourceFilePaths first
    /// - typically an active torrent's own on-disk files, the direct
    /// cross-seed scenario - then fallbackCatalogAssets, by exact size,
    /// verifying each same-size candidate via VerifyFileEntryAsync/
    /// VerifyAsync until one proves byte-identical. Declares the WHOLE
    /// torrent a match only if every entry resolves - no partial credit,
    /// consistent with this project's exact-match-only philosophy.
    /// </summary>
    Task<WholeTorrentMatchResult> EvaluateAsync(
        TorrentMetadata candidateTorrent,
        IReadOnlyCollection<string> primarySourceFilePaths,
        IReadOnlyCollection<MediaAsset> fallbackCatalogAssets);

    /// <summary>
    /// Commits an already-EvaluateAsync-proven full match: hardlinks every
    /// resolved file entry into outputRoot/candidateTorrent.Name/... (via
    /// the existing direct-path ReconstructAsync/ReconstructFileEntryAsync
    /// overloads - the matched path is already known, no need to re-search),
    /// then writes candidateTorrentBytes to
    /// outputRoot/candidateTorrent.Name/candidateTorrent.Name + ".torrent".
    /// Refuses to act if match.IsFullMatch is false.
    /// </summary>
    Task<WholeTorrentReconstructionResult> CommitAsync(
        TorrentMetadata candidateTorrent,
        WholeTorrentMatchResult match,
        byte[] candidateTorrentBytes,
        string outputRoot);
}
