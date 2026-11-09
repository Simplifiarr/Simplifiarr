using Simplifiarr.Core.Utilities;

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
        AppPaths.DefaultQuarantinePath;

    public string HistoryPath { get; set; } =
        AppPaths.DefaultHistoryFile;

    public string CrossSeedTorrentStoragePath { get; set; } =
        AppPaths.DefaultCrossSeedTorrentPath;

    public List<DriveProfile> DriveProfiles { get; set; } =
        [];
}