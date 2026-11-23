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

    /// <summary>Jellyseerr or Overseerr: who asked for each title.</summary>
    public string SeerrUrl { get; set; } =
        "http://localhost:5055";

    public string SeerrApiKey { get; set; } =
        string.Empty;

    /// <summary>
    /// Storage suggests reviewing a title nobody has watched once it has
    /// been on the server this many months.
    /// </summary>
    public int StorageUnwatchedMonths { get; set; } = 6;

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
    /// The kinds of picture the Posters page offers (Jellyfin's names:
    /// "Primary", "Backdrop", "Logo"...). Set on the Posters page.
    /// </summary>
    public List<string> PictureTypes { get; set; } =
        [.. Models.PictureTypes.Defaults];

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

    /// <summary>When Scan runs by itself. Off by default.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AutoScanMode>))]
    public AutoScanMode AutoScan { get; set; } =
        AutoScanMode.Off;

    /// <summary>For <see cref="AutoScanMode.EveryHours"/>.</summary>
    public int AutoScanEveryHours { get; set; } = 6;

    /// <summary>For <see cref="AutoScanMode.Daily"/>: local time, "HH:mm".</summary>
    public string AutoScanDailyTime { get; set; } = "03:00";

    /// <summary>
    /// After an automatic scan, also search the indexers for cross-seeds.
    /// Nothing is added to the torrent client without you.
    /// </summary>
    public bool AutoScanCrossSeed { get; set; }

    /// <summary>
    /// A protected drive starts moving titles off above this used
    /// percentage, unless the drive has its own.
    /// </summary>
    public int StorageFullAtPercent { get; set; } = 90;

    /// <summary>
    /// Titles bigger than this (GB) are never suggested to move: they are
    /// left to move by hand, or to shrink with Size Reduction. 0: no limit.
    /// </summary>
    public int StorageLargestToMoveGb { get; set; } = 100;

    /// <summary>
    /// Titles added or changed within this many days are never suggested
    /// to move: a new episode may still be arriving, and new things are
    /// what people watch. 0: no wait.
    /// </summary>
    public int StorageRecentDays { get; set; } = 14;

    /// <summary>
    /// After an automatic scan, queue the suggested moves by itself. Off
    /// by default.
    /// </summary>
    public bool StorageAutoMove { get; set; }

    /// <summary>
    /// Detailed logging (every call to the other apps, every decision) is
    /// on until then. It turns itself off so the log doesn't grow for ever.
    /// </summary>
    public DateTime? DetailedLoggingUntilUtc { get; set; }

    /// <summary>When Size Reduction searches the indexers by itself. Off by default.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AutoScanMode>))]
    public AutoScanMode SizeReductionAuto { get; set; } =
        AutoScanMode.Off;

    /// <summary>For <see cref="AutoScanMode.EveryHours"/>; a week by default.</summary>
    public int SizeReductionEveryHours { get; set; } = 24 * 7;

    /// <summary>For <see cref="AutoScanMode.Daily"/>: local time, "HH:mm".</summary>
    public string SizeReductionDailyTime { get; set; } = "04:00";

    /// <summary>Drive entries searched automatically; empty means all of them.</summary>
    public List<string> SizeReductionDrives { get; set; } = [];

    /// <summary>
    /// Swap a finished Size Reduction download in by itself. Off: it waits
    /// for Swap now.
    /// </summary>
    public bool SizeReductionAutoSwap { get; set; }

    /// <summary>
    /// Which smaller releases Size Reduction offers, and what has been
    /// ignored. Set on the Size Reduction page.
    /// </summary>
    public EncodeRules SizeReductionRules { get; set; } = new();

    /// <summary>What Junk Cleanup looks for. Set on the Junk page.</summary>
    public JunkRules Junk { get; set; } = new();
}

public enum AutoScanMode
{
    Off,
    EveryHours,
    Daily
}