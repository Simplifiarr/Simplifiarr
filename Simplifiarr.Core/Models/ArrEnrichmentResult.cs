namespace Simplifiarr.Core.Models;

public class ArrEnrichmentResult
{
    public int TotalFetched { get; set; }

    public int Matched { get; set; }

    public int Unmatched { get; set; }

    public List<MediaAsset> MatchedAssets { get; set; } =
        [];
}
