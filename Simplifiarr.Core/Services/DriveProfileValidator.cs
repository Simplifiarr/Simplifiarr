using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Spots drive setups where folders overlap in a way that makes results
/// wrong: Simplifiarr tells "library" from "download" purely by which
/// folder a file is in, so the two must not be the same place.
/// </summary>
public static class DriveProfileValidator
{
    private sealed record Folder(string Label, string Path, bool IsLibrary);

    /// <summary>
    /// Plain-language problems with one drive's folders. Empty when the
    /// setup is fine.
    /// </summary>
    public static List<string> GetProblems(DriveProfile profile)
    {
        var problems = new List<string>();

        var download = Normalize(profile.DownloadPath);

        var libraries =
            new[]
            {
                new Folder("Movie library", Normalize(profile.MovieLibraryPath), true),
                new Folder("TV library", Normalize(profile.TvLibraryPath), true),
                new Folder("Book library", Normalize(profile.BookLibraryPath), true),
                new Folder("Audiobook library", Normalize(profile.AudiobookLibraryPath), true),
                new Folder("Music library", Normalize(profile.MusicLibraryPath), true),
                new Folder("Comic library", Normalize(profile.ComicLibraryPath), true)
            }
            .Where(folder => folder.Path.Length > 0)
            .ToList();

        var downloads =
            new[]
            {
                new Folder("Downloads folder", download, false),
                new Folder("Audiobook downloads folder", Normalize(profile.AudiobookDownloadPath), false),
                new Folder("Music downloads folder", Normalize(profile.MusicDownloadPath), false),
                new Folder("Comic downloads folder", Normalize(profile.ComicDownloadPath), false)
            }
            .Where(folder => folder.Path.Length > 0)
            .ToList();

        foreach (var library in libraries)
        {
            foreach (var downloadFolder in downloads)
            {
                if (Same(library.Path, downloadFolder.Path))
                {
                    problems.Add(
                        $"{library.Label} and {downloadFolder.Label} are the same folder ({library.Path}). " +
                        "Downloads and library files can't be told apart there.");
                }
                else if (PathContainment.IsPathUnderRoot(downloadFolder.Path, library.Path))
                {
                    problems.Add(
                        $"{library.Label} ({library.Path}) contains the {downloadFolder.Label} ({downloadFolder.Path}), " +
                        "so every download is also counted as a library file. Point the library at its own folder.");
                }
                else if (PathContainment.IsPathUnderRoot(library.Path, downloadFolder.Path))
                {
                    problems.Add(
                        $"{library.Label} ({library.Path}) is inside the {downloadFolder.Label} ({downloadFolder.Path}), " +
                        "so library files are also treated as downloads. Move the library outside the downloads folder.");
                }
                else
                {
                    continue;
                }

                // One message per library is enough: the other download
                // folders usually sit inside the main one and would only
                // repeat it.
                break;
            }
        }

        foreach (var library in libraries)
        {
            var same =
                libraries
                    .Where(other =>
                        !ReferenceEquals(other, library) &&
                        Same(other.Path, library.Path))
                    .ToList();

            // Report a shared folder once, from the first library that uses it.
            if (same.Count > 0 &&
                libraries.IndexOf(library) < same.Min(libraries.IndexOf))
            {
                problems.Add(
                    $"{library.Label} and {JoinLabels(same)} are the same folder ({library.Path}).");
            }

            var inside =
                libraries
                    .Where(other =>
                        !ReferenceEquals(other, library) &&
                        !Same(other.Path, library.Path) &&
                        PathContainment.IsPathUnderRoot(other.Path, library.Path))
                    .ToList();

            if (inside.Count > 0)
            {
                problems.Add(
                    $"{library.Label} ({library.Path}) contains the {JoinLabels(inside)}, " +
                    "so those files are counted in both.");
            }
        }

        return problems;
    }

    private static string JoinLabels(List<Folder> folders)
    {
        var labels = folders.Select(folder => folder.Label).ToList();

        return labels.Count == 1
            ? labels[0]
            : string.Join(", ", labels.Take(labels.Count - 1)) + " and " + labels[^1];
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(full) ?? string.Empty;

            // Keep "F:\" as it is; trim the trailing slash off anything deeper.
            return full.Length > root.Length
                ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : full;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool Same(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
