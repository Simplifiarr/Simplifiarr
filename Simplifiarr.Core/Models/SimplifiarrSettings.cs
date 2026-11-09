namespace Simplifiarr.Core.Models;

public class SimplifiarrSettings
{
    public string QbittorrentUrl { get; set; } =
        "http://localhost:8080";

    public string RadarrUrl { get; set; } =
        "http://localhost:7878";

    public string RadarrApiKey { get; set; } =
        string.Empty;

    public string SonarrUrl { get; set; } =
        "http://localhost:8989";

    public string SonarrApiKey { get; set; } =
        string.Empty;

    public string ProwlarrUrl { get; set; } =
        "http://localhost:9696";

    public string ProwlarrApiKey { get; set; } =
        string.Empty;

    public string JellyfinUrl { get; set; } =
        "http://localhost:8096";

    public string JellyfinApiKey { get; set; } =
        string.Empty;

    public string QuarantinePath { get; set; } =
        @"C:\ProgramData\Simplifiarr\Quarantine";

    public string HistoryPath { get; set; } =
        @"C:\ProgramData\Simplifiarr\History\history.json";

    public string CrossSeedTorrentStoragePath { get; set; } =
        @"C:\ProgramData\Simplifiarr\CrossSeedTorrents";

    public List<DriveProfile> DriveProfiles { get; set; } =
        [];
}