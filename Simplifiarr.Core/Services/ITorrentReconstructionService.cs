using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface ITorrentReconstructionService
{
    Task<TorrentReconstructionResult> ReconstructAsync(
        TorrentMetadata torrent,
        string candidateFilePath,
        string outputRoot);

    /// <summary>
    /// Searches catalogAssets for a same-size candidate, then verifies each
    /// in turn until one proves byte-identical (or none do), hardlinking
    /// into outputRoot. outputRoot must be on the same drive as whichever
    /// candidate matches, since hardlinks only work within one volume - a
    /// mismatch surfaces as a normal hardlink failure, not a special case.
    /// </summary>
    Task<TorrentReconstructionResult> ReconstructFromCatalogAsync(
        TorrentMetadata torrent,
        IReadOnlyCollection<MediaAsset> catalogAssets,
        string outputRoot);

    /// <summary>
    /// Same as ReconstructAsync, but for a single file entry of a multi-file
    /// torrent rather than a whole single-file torrent - candidateFilePath
    /// is verified against just that entry's own bytes/piece range.
    /// </summary>
    Task<TorrentReconstructionResult> ReconstructFileEntryAsync(
        TorrentMetadata torrent,
        TorrentFileEntry fileEntry,
        string candidateFilePath,
        string outputRoot);

    /// <summary>
    /// Same as ReconstructFromCatalogAsync, but for a single file entry of a
    /// multi-file torrent - candidates are filtered by the entry's own
    /// LengthBytes, not the whole torrent's TotalLengthBytes.
    /// </summary>
    Task<TorrentReconstructionResult> ReconstructFileEntryFromCatalogAsync(
        TorrentMetadata torrent,
        TorrentFileEntry fileEntry,
        IReadOnlyCollection<MediaAsset> catalogAssets,
        string outputRoot);
}
