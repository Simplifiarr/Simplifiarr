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
    //   file, so nothing was freed. Download Cleanup's message says so.
    private static readonly (HistoryActionType Type, string Tool, string Href, string One, string Many)[] Tools =
    [
        (HistoryActionType.ConversionSucceeded, "Copies made hardlinks", "potential-waste", "file", "files"),
        (HistoryActionType.DuplicateQuarantined, "Duplicates", "duplicates", "copy", "copies"),
        (HistoryActionType.SizeReduced, "Size Reduction", "size-reduction", "file", "files"),
        (HistoryActionType.JunkFileQuarantined, "Junk Cleanup", "junk-cleanup", "file", "files"),
        (HistoryActionType.DownloadCopyRemoved, "Download Cleanup", "download-cleanup", "file", "files"),
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
            restored.TryGetValue(record.QuarantinePath, out var when) &&
            record.TimestampUtc <= when;

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

        return result.OrderByDescending(saving => saving.Bytes).ToList();
    }
}
