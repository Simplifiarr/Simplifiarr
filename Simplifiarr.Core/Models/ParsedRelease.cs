namespace Simplifiarr.Core.Models;

public class ParsedRelease
{
    public string OriginalName { get; set; } =
        string.Empty;

    public string NormalizedTitle { get; set; } =
        string.Empty;

    public int? Year { get; set; }

    public bool IsTv { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string Resolution { get; set; } =
        string.Empty;

    public string Codec { get; set; } =
        string.Empty;

    /// <summary>
    /// "Sonarr" or "Radarr" when the title came from their reading of the
    /// name; empty when it came from Simplifiarr's own.
    /// </summary>
    public string IdentifiedBy { get; set; } =
        string.Empty;

    /// <summary>The show's or film's folder, when Sonarr or Radarr already has it.</summary>
    public string? KnownFolder { get; set; }

    /// <summary>
    /// The show or film folder to create when there isn't one yet, named
    /// with Sonarr's or Radarr's folder format. Null means Simplifiarr's own
    /// default.
    /// </summary>
    public string? FolderName { get; set; }

    /// <summary>The season folder name, from Sonarr's format. Null means Simplifiarr's own default.</summary>
    public string? SeasonFolderName { get; set; }

    /// <summary>False when Sonarr keeps this show without season folders.</summary>
    public bool UsesSeasonFolders { get; set; } = true;

    public ParsedRelease Clone() =>
        (ParsedRelease)MemberwiseClone();
}

