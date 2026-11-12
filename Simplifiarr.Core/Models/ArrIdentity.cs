namespace Simplifiarr.Core.Models;

/// <summary>
/// What Sonarr or Radarr makes of a release name, from their own parser.
/// When they already have the show or film, <see cref="KnownFolder"/> is
/// the folder they keep it in.
/// </summary>
public sealed record ArrIdentity
{
    /// <summary>"Sonarr" or "Radarr".</summary>
    public string Source { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public int? Year { get; init; }

    public int? Season { get; init; }

    public int? Episode { get; init; }

    /// <summary>The show's or film's folder, when Sonarr or Radarr already has it.</summary>
    public string? KnownFolder { get; init; }

    /// <summary>False when the show is set up without season folders.</summary>
    public bool UsesSeasonFolders { get; init; } = true;

    public int? TvdbId { get; init; }

    public int? TmdbId { get; init; }

    public string? ImdbId { get; init; }
}

/// <summary>The folder formats from Sonarr's and Radarr's Media Management settings.</summary>
public sealed record ArrFolderFormats
{
    public string SeriesFolderFormat { get; init; } = "{Series Title}";

    public string SeasonFolderFormat { get; init; } = "Season {season:00}";

    public string SpecialsFolderFormat { get; init; } = "Specials";

    public string MovieFolderFormat { get; init; } = "{Movie Title} ({Release Year})";
}
