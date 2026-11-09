namespace Simplifiarr.Core.Models;

public class BookPromotionCandidate
{
    public string SourcePath { get; set; } =
        string.Empty;

    public string Title { get; set; } =
        string.Empty;

    public string? Author { get; set; }

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
