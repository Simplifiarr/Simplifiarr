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

    /// <summary>Images that aren't named like artwork (poster.jpg, fanart.jpg...).</summary>
    public bool UnrecognizedImages { get; set; } = true;

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

        return value.Length > 1 && value.Length <= 16 && value[1..].All(char.IsLetterOrDigit) ? value : null;
    }
}
