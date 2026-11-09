using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface ITorrentPieceVerificationService
{
    Task<PieceVerificationResult> VerifyAsync(
        TorrentMetadata torrent,
        string candidateFilePath);

    /// <summary>
    /// Verifies one file entry of a multi-file torrent against a candidate
    /// file, using only that candidate's own bytes. Pieces that straddle
    /// into a neighboring file entry can't be checked this way - they're
    /// reported separately as BoundaryPieceCount rather than counted as
    /// failures.
    /// </summary>
    Task<PieceVerificationResult> VerifyFileEntryAsync(
        TorrentMetadata torrent,
        TorrentFileEntry fileEntry,
        string candidateFilePath);

    /// <summary>
    /// Re-hashes a multi-file torrent's entire virtual byte stream, reading
    /// each matched file in torrent order, and checks every single piece -
    /// including the boundary pieces VerifyFileEntryAsync has to skip. Only
    /// when this passes is every byte of the torrent proven identical.
    /// </summary>
    Task<PieceVerificationResult> VerifyWholeTorrentAsync(
        TorrentMetadata torrent,
        IReadOnlyList<FileEntryMatch> entryMatches);
}
