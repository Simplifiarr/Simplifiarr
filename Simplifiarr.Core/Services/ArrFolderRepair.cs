using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>A Radarr movie or Sonarr show whose folder is gone, and where it may be now.</summary>
public sealed record ArrFolderProblem(
    string Kind,
    ArrItem Item,
    string? FoundFolder,
    int Matches,
    bool FolderIsEmpty = false)
{
    public bool CanFix => FoundFolder is not null;

    /// <summary>
    /// Nothing of it is on disk anywhere Simplifiarr can see, so the entry
    /// can only be removed.
    /// </summary>
    public bool CanRemove => FoundFolder is null && Matches == 0;

    public string Note =>
        FoundFolder is not null
            ? "Found in your libraries"
            : Matches > 1
                ? $"{Matches} folders could be it; fix it in {Item.Source}"
                : (FolderIsEmpty
                      ? "Its folder is there but has no files, and no other copy was found"
                      : "Not found in any library folder or root folder") +
                  (Item.Monitored ? $". It is monitored, so {Item.Source} may still be looking for it" : string.Empty);

    // (A monitored entry with nothing found anywhere isn't listed at all: see ArrFolderRepair.Find.)
}

/// <summary>
/// Finds Radarr movies and Sonarr shows pointing at folders that no longer
/// exist (moved by hand, or by an older tool), and the library folder each
/// one is in now. Only reads; fixing is a separate step you ask for.
/// </summary>
public static class ArrFolderRepair
{
    public static List<ArrFolderProblem> Find(
        string kind,
        IReadOnlyList<ArrItem> items,
        IReadOnlyList<StorageTitle> libraryTitles,
        Func<string, bool> folderExists,
        Func<string, bool>? folderHasFiles = null)
    {
        // A folder another item already uses is never offered.
        var claimed =
            items
                .Select(item => Trim(item.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var folders =
            libraryTitles
                .Where(title => title.Kind == kind)
                .Select(title => Trim(title.FolderPath))
                .Where(folder => !claimed.Contains(folder))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        var problems = new List<ArrFolderProblem>();

        foreach (var item in items)
        {
            // Not released yet: of course there are no files.
            if (item.Upcoming)
            {
                continue;
            }

            var exists = folderExists(item.Path);
            var empty = exists && folderHasFiles is not null && !folderHasFiles(item.Path);

            if (exists && !empty)
            {
                continue;
            }

            var name = Path.GetFileName(Trim(item.Path));

            // Same folder name first; otherwise the same title and year.
            var matches =
                folders.Where(folder => string.Equals(Path.GetFileName(folder), name, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matches.Count == 0)
            {
                var wanted = Key(item.Title, item.Year);

                matches =
                    folders
                        .Where(folder => Key(Path.GetFileName(folder), null) == wanted ||
                                         (item.Year is null && Key(Path.GetFileName(folder), null) == Key(item.Title, null)))
                        .ToList();
            }

            // Monitored with nothing on disk anywhere: Radarr or Sonarr is
            // still waiting to download it (often a Seerr request). Not a
            // problem, and removing it would lose the request.
            if (item.Monitored && matches.Count == 0)
            {
                continue;
            }

            problems.Add(new ArrFolderProblem(kind, item, matches.Count == 1 ? matches[0] : null, matches.Count, empty));
        }

        return problems
            .OrderByDescending(problem => problem.CanFix)
            .ThenBy(problem => problem.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"The Gorge (2025)" and "The Gorge", 2025 give the same key.</summary>
    private static string Key(string title, int? year)
    {
        var text = year is null ? title : $"{title} {year}";

        return DuplicateAssetAnalysisService.NormalizeTitle(
            text.Replace("(", " ").Replace(")", " "));
    }

    private static string Trim(string path) =>
        Path.TrimEndingDirectorySeparator(path.Replace('/', '\\'));
}
