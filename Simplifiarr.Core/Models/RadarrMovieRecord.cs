namespace Simplifiarr.Core.Models;

public class RadarrMovieRecord
{
    public int TmdbId { get; set; }

    public string Title { get; set; } =
        string.Empty;

    public int? Year { get; set; }

    public string? FilePath { get; set; }

    public List<string> Genres { get; set; } =
        [];

    public int QualityProfileId { get; set; }

    public bool Monitored { get; set; }
}
