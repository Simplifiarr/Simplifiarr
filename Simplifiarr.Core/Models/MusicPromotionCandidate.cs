namespace Simplifiarr.Core.Models;

public class MusicPromotionCandidate
{
    public string SourcePath { get; set; } =
        string.Empty;

    public string Title { get; set; } =
        string.Empty;

    public string? Artist { get; set; }

    public string? Album { get; set; }

    public double? TrackNumber { get; set; }

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
