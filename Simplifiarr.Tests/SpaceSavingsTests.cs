using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class SpaceSavingsTests
{
    private static readonly DateTime Start = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static HistoryRecord Record(
        HistoryActionType type,
        long size,
        int day = 0,
        bool success = true,
        string library = @"D:\Movies\A\a.mkv",
        string quarantine = "",
        string message = "") =>
        new()
        {
            ActionType = type,
            SizeBytes = size,
            TimestampUtc = Start.AddDays(day),
            Success = success,
            LibraryPath = library,
            QuarantinePath = quarantine,
            Message = message
        };

    [TestMethod]
    public void Each_tool_is_added_up_biggest_first()
    {
        var savings =
            SpaceSavings.Tally(
            [
                Record(HistoryActionType.SizeReduced, 300),
                Record(HistoryActionType.SizeReduced, 200),
                Record(HistoryActionType.DuplicateQuarantined, 900),
                Record(HistoryActionType.EpisodeRolledOff, 50),
                Record(HistoryActionType.MovieRolledOff, 1, success: false)
            ]);

        CollectionAssert.AreEqual(new[] { "Duplicates", "Size Reduction", "Rolling Shows" }, savings.Select(saving => saving.Tool).ToList());

        var reduced = savings.Single(saving => saving.Tool == "Size Reduction");
        Assert.AreEqual(500, reduced.Bytes);
        Assert.AreEqual(2, reduced.Count);
        Assert.AreEqual("files", reduced.Things);
        Assert.AreEqual("episode", savings.Single(saving => saving.Tool == "Rolling Shows").Things);
    }

    [TestMethod]
    public void Nothing_is_counted_twice()
    {
        var savings =
            SpaceSavings.Tally(
            [
                Record(HistoryActionType.ConversionSucceeded, 100, quarantine: @"Q:\a.mkv"),
                Record(HistoryActionType.ConversionSucceeded, 100, quarantine: @"Q:\b.mkv"),
                // The batch sums the two above, and emptying Quarantine frees what they counted.
                Record(HistoryActionType.BatchCompleted, 200),
                Record(HistoryActionType.QuarantineDeleted, 100, day: 1, quarantine: @"Q:\a.mkv"),
                // A rolled-off episode's torrent removed after seeding shares its space.
                Record(HistoryActionType.EpisodeRolledOff, 40),
                Record(HistoryActionType.EpisodeRolledOff, 40, day: 7, library: ""),
                // A download hardlinked to the library was the same file.
                Record(HistoryActionType.DownloadCopyRemoved, 70, message: "Download copy deleted - qBittorrent no longer has it and the library has the same file (hardlinked)."),
                Record(HistoryActionType.DownloadCopyRemoved, 30, message: "Download copy deleted - qBittorrent no longer has it and the library has an identical copy.")
            ]);

        Assert.AreEqual(200, savings.Single(saving => saving.Tool == "Copies made hardlinks").Bytes);
        Assert.AreEqual(40, savings.Single(saving => saving.Tool == "Rolling Shows").Bytes);
        Assert.AreEqual(30, savings.Single(saving => saving.Tool == "Download Cleanup").Bytes);
        Assert.AreEqual(270, savings.Sum(saving => saving.Bytes));
    }

    [TestMethod]
    public void Restoring_from_quarantine_takes_it_back_off()
    {
        var history = new List<HistoryRecord>
        {
            Record(HistoryActionType.JunkFileQuarantined, 10, quarantine: @"Q:\sample.mkv"),
            Record(HistoryActionType.JunkFileQuarantined, 5, quarantine: @"Q:\other.nfo"),
            Record(HistoryActionType.QuarantineRestored, 10, day: 1, quarantine: @"q:\SAMPLE.mkv")
        };

        Assert.AreEqual(5, SpaceSavings.Tally(history).Single().Bytes);

        // Quarantined again after the restore: it counts again.
        history.Add(Record(HistoryActionType.JunkFileQuarantined, 10, day: 2, quarantine: @"Q:\sample.mkv"));
        Assert.AreEqual(15, SpaceSavings.Tally(history).Single().Bytes);
    }

    [TestMethod]
    public void An_empty_history_has_no_savings() =>
        Assert.IsEmpty(SpaceSavings.Tally([]));
}
