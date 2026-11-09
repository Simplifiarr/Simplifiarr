namespace Simplifiarr.Core.Models;

public class AssetTorrentMatch
{
    public Guid MediaAssetId { get; set; }

    public string AssetTitle { get; set; } =
        string.Empty;

    public Guid TorrentId { get; set; }

    public string TorrentName { get; set; } =
        string.Empty;

    public string MatchReason { get; set; } =
        string.Empty;

    public int Confidence { get; set; }

    public string AssetResolution { get; set; } =
        string.Empty;

    public string AssetCodec { get; set; } =
        string.Empty;

    public string TorrentResolution { get; set; } =
        string.Empty;

    public string TorrentCodec { get; set; } =
        string.Empty;

    public long AssetSizeBytes { get; set; }

    public long TorrentSizeBytes { get; set; }

    public long SizeDifferenceBytes { get; set; }

    public string ScoreExplanation { get; set; } =
        string.Empty;

    public ScoreBreakdown Breakdown { get; set; } =
        new();
}



