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
}