namespace Simplifiarr.Core.Models;

public class MediaAsset
{
    public Guid Id { get; set; } =
        Guid.NewGuid();

    public string Title { get; set; } =
        string.Empty;

    public int? Year { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string? EpisodeTitle { get; set; }

    public string? Author { get; set; }

    /// <summary>
    /// Album title (MediaType.Music) or series title (MediaType.Comic).
    /// </summary>
    public string? Album { get; set; }

    /// <summary>
    /// Track number within its album (MediaType.Music) or issue number
    /// within its series (MediaType.Comic) - a double since comic issues
    /// can be fractional (e.g. "#0.5" annuals).
    /// </summary>
    public double? TrackNumber { get; set; }

    public MediaType MediaType { get; set; }

    public string CanonicalPath { get; set; } =
        string.Empty;

    public long FileSizeBytes { get; set; }

    public string Resolution { get; set; } =
        string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public string VideoCodec { get; set; } =
        string.Empty;

    public string? FileHash { get; set; }

    public int? TmdbId { get; set; }

    public int? TvdbId { get; set; }

    public List<string> Genres { get; set; } =
        [];

    public int? QualityProfileId { get; set; }

    public bool? Monitored { get; set; }
}
