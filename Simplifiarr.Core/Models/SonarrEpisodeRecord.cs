namespace Simplifiarr.Core.Models;

public class SonarrEpisodeRecord
{
    public int TvdbId { get; set; }

    public string SeriesTitle { get; set; } =
        string.Empty;

    public int? Year { get; set; }

    public string? FilePath { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public string? EpisodeTitle { get; set; }

    public List<string> Genres { get; set; } =
        [];

    public int QualityProfileId { get; set; }

    public bool Monitored { get; set; }
}
