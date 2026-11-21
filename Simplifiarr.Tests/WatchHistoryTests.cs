using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class WatchHistoryTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static JellyfinWatchItem Movie(string id, string path, bool watched, int monthsAgo = 1) =>
        new(id, false, path, Now.AddMonths(-monthsAgo), watched, watched, watched ? Now.AddDays(-3) : null);

    private static StorageTitle Title(string folder, string kind = StorageKinds.Movies, long size = 100) =>
        new() { Name = Path.GetFileName(folder), FolderPath = folder, Kind = kind, SizeBytes = size };

    [TestMethod]
    public void Watching_is_gathered_from_every_user_and_the_requester_is_marked()
    {
        var records =
            WatchHistoryService.Combine(
                [
                    ("u-1", "Alex", [Movie("m1", @"D:\Movies\Harbour (2019)\Harbour.mkv", watched: true)]),
                    ("u-2", "Sam", [Movie("m1", @"D:\Movies\Harbour (2019)\Harbour.mkv", watched: false)])
                ],
                [new SeerrRequest(42, "m1", "Sam", "u2", Now.AddMonths(-2))]);

        var record = records.Single();

        CollectionAssert.AreEqual(new[] { "Alex" }, record.WatchedBy);
        Assert.AreEqual("Sam", record.RequestedBy);
        Assert.IsFalse(record.RequesterWatched, "Ids match with or without dashes, and Sam hasn't watched it.");
        Assert.AreEqual(42, record.SeerrMediaId);
    }

    [TestMethod]
    public void A_movie_file_and_a_show_folder_both_match_their_title()
    {
        var records = new List<WatchRecord>
        {
            new() { Path = @"D:\Movies\Harbour (2019)\Harbour.mkv" },
            new() { Path = @"D:\TV\Night Shift", IsSeries = true }
        };

        Assert.IsNotNull(WatchHistoryService.Match(Title(@"D:\Movies\Harbour (2019)"), records));
        Assert.IsNotNull(WatchHistoryService.Match(Title(@"D:\TV\Night Shift", StorageKinds.Tv), records));
        Assert.IsNull(WatchHistoryService.Match(Title(@"D:\Movies\Harbour 2"), records));
    }

    [TestMethod]
    public void Review_lists_what_the_requester_watched_and_what_nobody_watched_for_months()
    {
        var watchedByRequester = Title(@"D:\Movies\A", size: 10);
        var oldUnwatched = Title(@"D:\Movies\B", size: 30);
        var newUnwatched = Title(@"D:\Movies\C", size: 50);
        var watchedNotRequested = Title(@"D:\Movies\D", size: 70);
        var book = Title(@"D:\Books\E", StorageKinds.Books, 90);

        var records = new Dictionary<StorageTitle, WatchRecord>
        {
            [watchedByRequester] = new() { RequestedBy = "Sam", RequesterWatched = true, WatchedBy = ["Sam"], StartedBy = ["Sam"], AddedUtc = Now.AddMonths(-1) },
            [oldUnwatched] = new() { AddedUtc = Now.AddMonths(-8) },
            [newUnwatched] = new() { AddedUtc = Now.AddMonths(-2) },
            [watchedNotRequested] = new() { WatchedBy = ["Alex"], StartedBy = ["Alex"], AddedUtc = Now.AddMonths(-12) },
            [book] = new() { AddedUtc = Now.AddMonths(-20) }
        };

        var reviews =
            WatchHistoryService.Review(
                records.Keys,
                title => records.GetValueOrDefault(title),
                6,
                Now);

        CollectionAssert.AreEqual(new[] { "B", "A" }, reviews.Select(review => review.Title.Name).ToList(), "Largest first.");
        StringAssert.Contains(reviews[0].Reason, "Nobody has watched it");
        StringAssert.Contains(reviews[1].Reason, "Sam asked for it");
    }
}

[TestClass]
public class StorageDeleteTests : TempFolderTest
{
    [TestMethod]
    public async Task Deleting_removes_it_from_Radarr_then_recycles_the_folder()
    {
        var library = Path.Combine(Folder, "Movies");
        var folder = Path.Combine(library, "Harbour (2019)");

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Harbour.mkv"), "film");

        var settings = new DriveSettings(new SimplifiarrSettings { DriveProfiles = [new DriveProfile { Name = "D", MovieLibraryPath = library }] });
        var arr = new FakeArrPaths { KnownFolder = folder };
        var recycled = new List<string>();

        var mover =
            new StorageMoveService(arr, new StorageService(settings, new FileInventoryCacheService(new FileScannerService())), new HistoryService(settings), new IdleScans())
            {
                RemoveOldFolder = path =>
                {
                    recycled.Add(path);
                    Directory.Delete(path, true);
                    return true;
                }
            };

        var result =
            await mover.DeleteAsync(
                new StorageTitle { Name = "Harbour (2019)", Kind = StorageKinds.Movies, FolderPath = folder },
                0);

        Assert.IsTrue(result.Succeeded, result.Message);
        CollectionAssert.AreEqual(new[] { 7 }, arr.Removed);
        CollectionAssert.AreEqual(new[] { folder }, recycled);
    }
}
