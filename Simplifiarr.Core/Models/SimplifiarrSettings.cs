using System.Text.Json.Serialization;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Models;

public class SimplifiarrSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter<TorrentClientKind>))]
    public TorrentClientKind TorrentClient { get; set; } =
        TorrentClientKind.QBittorrent;

    public string TorrentClientUrl { get; set; } =
        "http://localhost:8080";

    /// <summary>Blank when the client doesn't ask for a login.</summary>
    public string TorrentClientUsername { get; set; } =
        string.Empty;

    public string TorrentClientPassword { get; set; } =
        string.Empty;

    /// <summary>
    /// The address's old name, from before other clients were planned.
    /// Read from older settings files and carried over; never written.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QbittorrentUrl
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                TorrentClientUrl = value;
            }
        }
    }

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

    /// <summary>
    /// A personal fanart.tv API key. Optional: with it, the Posters page
    /// also offers fanart.tv's posters.
    /// </summary>
    public string FanartApiKey { get; set; } =
        string.Empty;

    /// <summary>
    /// The Windows account the media server runs as, for checking it can
    /// read library files. Blank: found from the Jellyfin, Emby or Plex
    /// Windows service.
    /// </summary>
    public string MediaServerAccount { get; set; } =
        string.Empty;

    /// <summary>
    /// Where 7z.exe is (the file or its folder), for unpacking packed
    /// releases. Blank: looked for in the usual 7-Zip install locations.
    /// </summary>
    public string SevenZipPath { get; set; } =
        string.Empty;

    /// <summary>
    /// Where ffprobe.exe is (the file or its folder). Leave blank to have
    /// Simplifiarr look in the usual install locations itself.
    /// </summary>
    public string FfprobePath { get; set; } =
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