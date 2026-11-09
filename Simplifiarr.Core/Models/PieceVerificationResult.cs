namespace Simplifiarr.Core.Models;

public class PieceVerificationResult
{
    public bool IsFullMatch { get; set; }

    public int VerifiedPieceCount { get; set; }

    public int TotalPieceCount { get; set; }

    /// <summary>
    /// Pieces that overlap this file's range but also extend into a
    /// neighboring file within the same multi-file torrent, so they can't
    /// be verified from this one candidate alone - not a failure, an
    /// inherent limit of checking one file in isolation. Always 0 for
    /// single-file torrents.
    /// </summary>
    public int BoundaryPieceCount { get; set; }

    public string? FailureReason { get; set; }
}
