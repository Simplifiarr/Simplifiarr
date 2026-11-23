namespace Simplifiarr.Core.Models;

/// <summary>What Junk Cleanup treats as junk. Each person picks their own.</summary>
public class JunkRules
{
    /// <summary>What a new install starts with.</summary>
    public static readonly IReadOnlyList<string> DefaultExtensions =
        [".sfv", ".md5", ".torrent", ".url", ".txt", ".json", ".xml"];

    /// <summary>Offered on the page as one-click additions, with why someone might want them.</summary>
    public static readonly IReadOnlyList<(string Extension, string Note)> Suggestions =
    [
        (".nfo", "Release info. Jellyfin, Plex and Kodi can read their own .nfo files, so only add this if you don't use them."),
        (".exe", "Programs don't belong in a media library."),
        (".lnk", "Windows shortcuts."),
        (".srr", "Scene release reconstruction files."),
        (".srs", "Scene sample reconstruction files."),
        (".par2", "Repair files from Usenet downloads."),
        (".db", "Thumbs.db and other Windows thumbnail caches."),
        (".ini", "desktop.ini folder settings.")
    ];

    /// <summary>Sample clips: "sample.mkv", "Movie-sample.mkv" and the like.</summary>
    public bool SampleVideos { get; set; } = true;

    /// <summary>Anything inside a release's Sample folder.</summary>
    public bool SampleFolderLeftovers { get; set; } = true;

    /// <summary>File types that are always junk, such as ".sfv".</summary>
    public List<string> Extensions { get; set; } = [.. DefaultExtensions];

    /// <summary>
    /// The kinds of picture media players read from next to the media, and
    /// the file names each goes by. "season01-poster.jpg" and numbered extras
    /// ("backdrop2.jpg") count as the same kind.
    /// </summary>
    public static readonly IReadOnlyList<(string Kind, string Label, string[] Names)> PictureKinds =
    [
        ("Poster", "Posters", ["folder", "poster", "cover"]),
        ("Backdrop", "Backdrops", ["backdrop", "fanart", "background"]),
        ("Logo", "Logos", ["logo", "clearlogo"]),
        ("Landscape", "Landscape", ["landscape", "thumb"]),
        ("Banner", "Banners", ["banner"]),
        ("ClearArt", "Clear art", ["clearart", "art"]),
        ("Disc", "Disc art", ["disc", "discart"]),
        ("Box", "Box art", ["box", "boxrear", "back"]),
        ("KeyArt", "Key art", ["keyart"]),
        ("CharacterArt", "Character art", ["characterart"]),
        ("Menu", "Menu art", ["menu"]),
        ("Screenshot", "Screenshots", ["screenshot"])
    ];

    /// <summary>Images that aren't a picture kind you keep (poster.jpg, fanart.jpg...).</summary>
    public bool UnrecognizedImages { get; set; } = true;

    /// <summary>
    /// Picture kinds (<see cref="PictureKinds"/>) unticked on the page: their
    /// files count as junk too. Stored this way round so a kind added later
    /// starts out kept.
    /// </summary>
    public List<string> UnwantedPictures { get; set; } = [];

    /// <summary>
    /// The picture kind an image's name is ("season01-poster" -> Poster,
    /// "backdrop2" -> Backdrop), or null when it isn't named like one.
    /// </summary>
    public static (string Kind, string Label)? PictureKindOf(string nameWithoutExtension)
    {
        var name = nameWithoutExtension.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        foreach (var (kind, label, names) in PictureKinds)
        {
            if (names.Any(known =>
                    string.Equals(known, name, StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith($"-{known}", StringComparison.OrdinalIgnoreCase)))
            {
                return (kind, label);
            }
        }

        return null;
    }

    /// <summary>File names that are junk, with * as a wildcard: "RARBG*.txt".</summary>
    public List<string> JunkNames { get; set; } = [];

    /// <summary>File names that are never junk, whatever else says so: "theme.mp3", "*.nfo".</summary>
    public List<string> KeepNames { get; set; } = [];

    /// <summary>".SFV", "sfv" or "*.sfv" -> ".sfv"; null when it isn't a file type.</summary>
    public static string? NormalizeExtension(string? text)
    {
        var value = (text ?? string.Empty).Trim().TrimStart('*').Trim().ToLowerInvariant();

        if (value.Length == 0)
        {
            return null;
        }

        if (!value.StartsWith('.'))
        {
            value = "." + value;
        }

        // Letters and digits, plus the marks some apps use: ".!qB" for
        // qBittorrent's unfinished files, ".part~" and the like.
        return value.Length > 1 && value.Length <= 16 && value[1..].All(c => char.IsLetterOrDigit(c) || "!_-~$#@+".Contains(c))
            ? value
            : null;
    }
}
