namespace Simplifiarr.Core.Models;

/// <summary>
/// What Simplifiarr could read about one book, audiobook, track or comic,
/// in one shape for every type. Types that have no use for a field leave
/// it empty.
/// </summary>
public class MediaFileMetadata
{
    public string Title { get; set; } =
        string.Empty;

    /// <summary>
    /// Author for books, audiobooks and comics; artist for music.
    /// </summary>
    public string? Author { get; set; }

    public string? Narrator { get; set; }

    /// <summary>
    /// Album for music; series for comics.
    /// </summary>
    public string? Album { get; set; }

    /// <summary>
    /// Track number for music; issue number for comics (which can be
    /// fractional, e.g. an annual numbered 0.5).
    /// </summary>
    public double? TrackNumber { get; set; }

    public int? Year { get; set; }

    /// <summary>
    /// True when this came from tags inside the file rather than being
    /// guessed from the file's name.
    /// </summary>
    public bool FromEmbeddedTags { get; set; }
}

/// <summary>
/// A downloaded book, audiobook, track or comic that isn't in its library
/// yet and could be hardlinked into it.
/// </summary>
public class MediaPromotionCandidate
{
    public MediaType MediaType { get; set; }

    public string SourcePath { get; set; } =
        string.Empty;

    public string Title { get; set; } =
        string.Empty;

    public string? Author { get; set; }

    public string? Narrator { get; set; }

    public string? Album { get; set; }

    public double? TrackNumber { get; set; }

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
