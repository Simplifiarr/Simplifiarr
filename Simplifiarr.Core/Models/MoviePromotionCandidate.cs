namespace Simplifiarr.Core.Models;

public class MoviePromotionCandidate
{
    public string SourcePath { get; set; } =
        string.Empty;

    public string Title { get; set; } =
        string.Empty;

    public int? Year { get; set; }

    public bool IsTv { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public DateTime DiscoveredUtc { get; set; }

    public string DriveProfileName { get; set; } =
        string.Empty;

    public PromotionPlan? Plan { get; set; }

    public bool CanPromote { get; set; }

    public string Reason { get; set; } =
        string.Empty;

    public bool WasPreviouslyPromoted { get; set; }

    public string? PreviousLibraryPath { get; set; }
}