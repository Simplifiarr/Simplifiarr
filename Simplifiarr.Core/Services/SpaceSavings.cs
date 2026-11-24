using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Adds up what each tool has freed. Pure: works only from History records.
/// </summary>
public static class SpaceSavings
{
    // Each tool's own action, counted once when it happens. Things sent to
    // Quarantine count then too, and come off again if they're restored.
    // Left out on purpose:
    // - QuarantineDeleted: emptying Quarantine frees what a tool already counted.
    // - BatchCompleted: a sum of the conversions already counted one by one.
    // - A rolled-off torrent removed after seeding: it shares its space with
    //   the episode or movie already counted.
    // - A download deleted while hardlinked to the library: it was the same
    //   file, so nothing was freed. Its message says so.
    // Several actions can belong to one tool: Copies links, deletes and
    // removes versions, and pages that did those before it count there too.
    private static readonly (HistoryActionType Type, string Tool, string Href, string One, string Many)[] Tools =
    [
        (HistoryActionType.ConversionSucceeded, "Copies", "copies", "file", "files"),
        (HistoryActionType.DuplicateQuarantined, "Copies", "copies", "file", "files"),
        (HistoryActionType.DownloadCopyRemoved, "Copies", "copies", "file", "files"),
        (HistoryActionType.SizeReduced, "Size Reduction", "size-reduction", "file", "files"),
        (HistoryActionType.MusicConverted, "Music converted", "music", "track", "tracks"),
        (HistoryActionType.JunkFileQuarantined, "Junk Cleanup", "junk-cleanup", "file", "files"),
        (HistoryActionType.SeedMoved, "Move Seeds", "move-seeds", "torrent", "torrents"),
        (HistoryActionType.TitleDeleted, "Deleted from Storage", "storage", "title", "titles"),
        (HistoryActionType.EpisodeRolledOff, "Rolling Shows", "rolling-shows", "episode", "episodes"),
        (HistoryActionType.MovieRolledOff, "Rolling Movies", "rolling-movies", "movie", "movies")
    ];

    /// <summary>Every tool that has freed something, biggest first.</summary>
    public static IReadOnlyList<ToolSaving> Tally(IEnumerable<HistoryRecord> history)
    {
        var records = history.Where(record => record.Success && record.SizeBytes > 0).ToList();

        // Quarantine path -> when it was last restored. Something quarantined
        // again after that still counts.
        var restored =
            records
                .Where(record => record.ActionType == HistoryActionType.QuarantineRestored && record.QuarantinePath.Length > 0)
                .GroupBy(record => record.QuarantinePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Max(record => record.TimestampUtc), StringComparer.OrdinalIgnoreCase);

        bool WasRestored(HistoryRecord record) =>
            record.QuarantinePath.Length > 0 &&
            restored.Any(entry =>
                (string.Equals(entry.Key, record.QuarantinePath, StringComparison.OrdinalIgnoreCase) ||
                 Utilities.PathContainment.IsPathUnderRoot(entry.Key, record.QuarantinePath)) &&
                record.TimestampUtc <= entry.Value);

        var result = new List<ToolSaving>();

        foreach (var (type, tool, href, one, many) in Tools)
        {
            var counted =
                records
                    .Where(record => record.ActionType == type)
                    .Where(record => !(type == HistoryActionType.EpisodeRolledOff && record.LibraryPath.Length == 0))
                    .Where(record => !(type == HistoryActionType.DownloadCopyRemoved && record.Message.Contains("(hardlinked)", StringComparison.Ordinal)))
                    .Where(record => !WasRestored(record))
                    .ToList();

            if (counted.Count > 0)
            {
                result.Add(new ToolSaving(tool, href, counted.Count, counted.Sum(record => record.SizeBytes), counted.Count == 1 ? one : many));
            }
        }

        return result
            .GroupBy(saving => saving.Tool)
            .Select(group =>
            {
                var count = group.Sum(saving => saving.Count);
                var first = Tools.First(tool => tool.Tool == group.Key);

                return new ToolSaving(group.Key, first.Href, count, group.Sum(saving => saving.Bytes), count == 1 ? first.One : first.Many);
            })
            .OrderByDescending(saving => saving.Bytes)
            .ToList();
    }
}
