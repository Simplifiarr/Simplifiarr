namespace Simplifiarr.Core.Models;

public class ScoreBreakdown
{
    public int TitlePoints { get; set; }

    public int SizePoints { get; set; }

    public int ResolutionPenalty { get; set; }

    public int CodecPenalty { get; set; }

    public int TotalScore { get; set; }
}
