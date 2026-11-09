namespace Simplifiarr.Core.Models;

public class AssetMatchAnalysis
{
    public Guid MediaAssetId { get; set; }

    public string AssetTitle { get; set; } =
        string.Empty;

    public int? AssetYear { get; set; }

    public bool IsMatched { get; set; }

    public AssetRelationshipType RelationshipType { get; set; }

    public string Reason { get; set; } =
        string.Empty;

    public int CandidateCount { get; set; }

    public string BestCandidateName { get; set; } =
        string.Empty;

    public int BestCandidateScore { get; set; }

    public int SecondBestCandidateScore { get; set; }
}


