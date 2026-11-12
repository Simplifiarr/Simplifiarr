namespace Simplifiarr.Core.Models;

public class LibraryTarget
{
    public string TargetDirectory { get; set; } =
        string.Empty;

    public string TargetFilePath { get; set; } =
        string.Empty;

    public bool DirectoryExists { get; set; }

    public bool FileExists { get; set; }

    public bool IsTv { get; set; }

    /// <summary>
    /// Set when Sonarr or Radarr keeps this show or film in a folder outside
    /// this drive's library. A hardlink can't reach it, so nothing is offered.
    /// </summary>
    public string? KeptElsewhere { get; set; }
}