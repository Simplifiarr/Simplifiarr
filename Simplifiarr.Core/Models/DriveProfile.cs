using System.Text.Json.Serialization;

namespace Simplifiarr.Core.Models;

public class DriveProfile
{
    public string Name { get; set; } =
        string.Empty;

    public string DriveRoot { get; set; } =
        string.Empty;

    public bool Enabled { get; set; } = true;

    public string DownloadPath { get; set; } =
        string.Empty;

    /// <summary>
    /// Where movie downloads go. Blank: the main <see cref="DownloadPath"/>.
    /// When set, only movies are looked for here.
    /// </summary>
    public string MovieDownloadPath { get; set; } =
        string.Empty;

    /// <summary>Where TV downloads go. Blank: the main downloads folder.</summary>
    public string TvDownloadPath { get; set; } =
        string.Empty;

    /// <summary>Where book downloads go. Blank: the main downloads folder.</summary>
    public string BookDownloadPath { get; set; } =
        string.Empty;

    /// <summary>The folder movies are downloaded to, falling back to the main one.</summary>
    [JsonIgnore]
    public string MovieDownloads => OrMain(MovieDownloadPath);

    [JsonIgnore]
    public string TvDownloads => OrMain(TvDownloadPath);

    [JsonIgnore]
    public string BookDownloads => OrMain(BookDownloadPath);

    /// <summary>
    /// Every downloads folder this drive has, main and per-kind, without
    /// blanks or repeats. The seed-only folder is not included.
    /// </summary>
    public List<string> AllDownloadPaths() =>
        new[]
            {
                DownloadPath,
                MovieDownloadPath,
                TvDownloadPath,
                BookDownloadPath,
                AudiobookDownloadPath,
                MusicDownloadPath,
                ComicDownloadPath
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private string OrMain(string path) =>
        string.IsNullOrWhiteSpace(path) ? DownloadPath : path;

    public string MovieLibraryPath { get; set; } =
        string.Empty;

    public string TvLibraryPath { get; set; } =
        string.Empty;

    public string BookLibraryPath { get; set; } =
        string.Empty;

    public string AudiobookLibraryPath { get; set; } =
        string.Empty;

    /// <summary>
    /// Dedicated download-side folder for audiobook downloads - kept
    /// separate from the shared DownloadPath because audiobook and music
    /// files use the same formats (MP3/M4A/M4B) and can't be told apart
    /// by content alone. Left blank, no audiobook download candidates are
    /// suggested (safe default).
    /// </summary>
    public string AudiobookDownloadPath { get; set; } =
        string.Empty;

    public string MusicLibraryPath { get; set; } =
        string.Empty;

    /// <summary>
    /// Dedicated download-side folder for music downloads - see
    /// AudiobookDownloadPath for why this can't share the common
    /// DownloadPath.
    /// </summary>
    public string MusicDownloadPath { get; set; } =
        string.Empty;

    public string ComicLibraryPath { get; set; } =
        string.Empty;

    /// <summary>
    /// Dedicated download-side folder for comic downloads - see
    /// AudiobookDownloadPath for the general reasoning; comics don't share
    /// an extension collision with another type, but a dedicated path is
    /// kept from day one to avoid ever repeating that class of bug.
    /// </summary>
    public string ComicDownloadPath { get; set; } =
        string.Empty;

    public string SeedOnlyPath { get; set; } =
        string.Empty;

    /// <summary>
    /// When true, promotion keeps the downloaded file's original filename
    /// instead of renaming it to the canonical "Title (Year)"/"Title -
    /// SxxExx" form. Folder structure is unaffected either way.
    /// </summary>
    public bool PreserveOriginalFileName { get; set; }

    /// <summary>
    /// A protected drive is kept clear: once it is fuller than
    /// <see cref="FullAtPercent"/>, Storage moves titles off it until it is
    /// back to <see cref="BringBackToPercent"/>. Nothing is moved onto it.
    /// Any other drive is an overflow drive: it takes what full drives send
    /// it, always leaving <see cref="KeepFreeGb"/> free.
    /// </summary>
    public bool IsProtected { get; set; }

    /// <summary>
    /// A protected drive starts moving titles off above this used
    /// percentage. Null: the default in Settings.
    /// </summary>
    public int? FullAtPercent { get; set; }

    /// <summary>
    /// A protected drive is brought back down to this used percentage.
    /// Null: 10 points under <see cref="FullAtPercent"/>.
    /// </summary>
    public int? BringBackToPercent { get; set; }

    /// <summary>
    /// An overflow drive always keeps this much free, in GB. Null: a tenth
    /// of the drive.
    /// </summary>
    public int? KeepFreeGb { get; set; }
}