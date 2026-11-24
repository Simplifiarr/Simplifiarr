using System.Text.Json.Serialization;

namespace Simplifiarr.Core.Models;

/// <summary>
/// One file on disk with every name the scan saw for it. Hardlinked names
/// are the same file, so they are one <see cref="CopyFile"/>: a library name
/// and a download name of one file take the space once.
/// </summary>
public class CopyFile
{
    public List<string> LibraryNames { get; set; } = [];

    public List<string> DownloadNames { get; set; } = [];

    public long SizeBytes { get; set; }

    public DateTime ModifiedUtc { get; set; }

    /// <summary>The drive entry in Settings this file belongs to.</summary>
    public string DriveName { get; set; } = string.Empty;

    /// <summary>"1080p", "720p"... or empty.</summary>
    public string Quality { get; set; } = string.Empty;

    /// <summary>The torrent using one of its names, or empty when none does.</summary>
    public string Torrent { get; set; } = string.Empty;

    /// <summary>True when the torrent client couldn't be asked: treated as seeding.</summary>
    public bool SeedingUnknown { get; set; }

    /// <summary>
    /// For a download nothing else shares a torrent folder with: the
    /// folder, and its subtitles, pictures and the like, which go with it.
    /// </summary>
    public string TorrentFolder { get; set; } = string.Empty;

    public List<string> TorrentFolderJunk { get; set; } = [];

    public bool TorrentFolderIsFolder { get; set; }

    [JsonIgnore]
    public bool Seeding => Torrent.Length > 0 || SeedingUnknown;

    [JsonIgnore]
    public bool InLibrary => LibraryNames.Count > 0;

    [JsonIgnore]
    public bool InDownloads => DownloadNames.Count > 0;

    /// <summary>The library name when there is one, else the download name.</summary>
    [JsonIgnore]
    public string Path => LibraryNames.FirstOrDefault() ?? DownloadNames.FirstOrDefault() ?? string.Empty;

    [JsonIgnore]
    public string FileName => System.IO.Path.GetFileName(Path);

    [JsonIgnore]
    public IEnumerable<string> Names => LibraryNames.Concat(DownloadNames);
}

/// <summary>
/// A library file and a download with the same bytes that are two files,
/// while a torrent still seeds the download from the same drive. The library
/// copy can become another name for the download: seeding carries on and
/// the space is taken once.
/// </summary>
public class CopyToLink
{
    public CopyFile Library { get; set; } = new();

    public CopyFile Download { get; set; } = new();

    /// <summary>The library file has other names too, so linking would free nothing.</summary>
    public bool LibraryHasOtherNames { get; set; }

    [JsonIgnore]
    public long FreedBytes => LibraryHasOtherNames ? 0 : Library.SizeBytes;
}

/// <summary>
/// The same movie, episode or album more than once in your library and
/// downloads, as different files. Keep one; the rest go to Quarantine.
/// </summary>
public class CopyVersionGroup
{
    public MediaType MediaType { get; set; }

    /// <summary>"movie|title|year", "tv|show|1|2", or the catalog's key for books and music.</summary>
    public string Identity { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string? Author { get; set; }

    /// <summary>Largest first.</summary>
    public List<CopyFile> Versions { get; set; } = [];

    /// <summary>Space freed by keeping only the largest version, counting only ones that can be removed.</summary>
    [JsonIgnore]
    public long ReclaimableBytes =>
        Versions.Skip(1).Where(version => !version.Seeding).Sum(version => version.SizeBytes);

    [JsonIgnore]
    public string DisplayTitle =>
        MediaType switch
        {
            MediaType.TvEpisode when Season is not null && Episode is not null => $"{Title} - S{Season:00}E{Episode:00}",
            MediaType.Book or MediaType.Audiobook or MediaType.Music or MediaType.Comic when !string.IsNullOrWhiteSpace(Author) => $"{Title} ({Author})",
            _ when Year is not null => $"{Title} ({Year})",
            _ => Title
        };
}

/// <summary>What one Copies scan found, across every drive at once.</summary>
public class CopiesScanResult
{
    public DateTime ScannedUtc { get; set; }

    /// <summary>False when the torrent client couldn't be reached: nothing is offered for deleting.</summary>
    public bool ClientReachable { get; set; } = true;

    public string ClientName { get; set; } = string.Empty;

    /// <summary>
    /// Download folders no torrent uses whose files your library has: the
    /// very same file (a hardlink), the same bytes, or only leftovers.
    /// </summary>
    public List<DownloadLeftover> ToDelete { get; set; } = [];

    /// <summary>Still seeding, same bytes as a library file on the same drive.</summary>
    public List<CopyToLink> ToLink { get; set; } = [];

    /// <summary>Titles there is more than one version of.</summary>
    public List<CopyVersionGroup> Versions { get; set; } = [];

    /// <summary>
    /// Every download name placed in one of the lists above, or already one
    /// file with a library name. "Not in your library" leaves these out, so
    /// each download is in one place only.
    /// </summary>
    public List<string> MatchedDownloads { get; set; } = [];

    /// <summary>Library files that are already one file with a download.</summary>
    public int AlreadyLinkedCount { get; set; }

    /// <summary><see cref="AlreadyLinkedCount"/> for each drive entry.</summary>
    public Dictionary<string, int> LinkedByDrive { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public long ExtraCopyBytes =>
        ToDelete.Sum(leftover => leftover.FreedBytes) + ToLink.Sum(link => link.FreedBytes);

    [JsonIgnore]
    public int ExtraCopyCount => ToDelete.Count + ToLink.Count;

    [JsonIgnore]
    public long VersionBytes => Versions.Sum(group => group.ReclaimableBytes);
}

public class CopiesActionResult
{
    public int Done { get; set; }

    public long FreedBytes { get; set; }

    public List<string> Skipped { get; set; } = [];

    public string? Error { get; set; }

    /// <summary>Something worth knowing about what was done, or null.</summary>
    public string? Note { get; set; }
}
