namespace Simplifiarr.Core.Models;

public class TorrentReconstructionResult
{
    public bool Success { get; set; }

    public string? TargetPath { get; set; }

    /// <summary>
    /// The folder the hardlink actually landed under - the configured
    /// storage path, or its same-named equivalent on the matched file's own
    /// drive when that's a different volume.
    /// </summary>
    public string? OutputRoot { get; set; }

    public PieceVerificationResult? VerificationResult { get; set; }

    /// <summary>
    /// Which cataloged asset actually verified - only populated by
    /// ReconstructFromCatalogAsync, since ReconstructAsync is handed a
    /// specific path directly and has no catalog concept.
    /// </summary>
    public MediaAsset? MatchedAsset { get; set; }

    public string? FailureReason { get; set; }
}
