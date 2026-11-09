namespace Simplifiarr.Core.Models;

public class ComicPromotionCandidate
{
    public string SourcePath { get; set; } =
        string.Empty;

    public string Title { get; set; } =
        string.Empty;

    public string? Series { get; set; }

    public double? Issue { get; set; }

    public string? Author { get; set; }

    public int? Year { get; set; }

    public string Format { get; set; } =
        string.Empty;

    public DateTime DiscoveredUtc { get; set; }

    public string DriveProfileName { get; set; } =
        string.Empty;

    public PromotionPlan? Plan { get; set; }

    public bool CanPromote { get; set; }

    public string Reason { get; set; } =
        string.Empty;
}
