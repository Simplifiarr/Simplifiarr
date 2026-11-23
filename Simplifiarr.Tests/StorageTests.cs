using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

[TestClass]
public class StoragePlannerTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static StorageTitle Title(string name, long gb, string kind = StorageKinds.Movies, long seedingGb = 0) =>
        new()
        {
            Name = name,
            Kind = kind,
            FolderPath = @"X:\Movies\" + name,
            SizeBytes = gb * Gb,
            SeedingBytes = seedingGb * Gb
        };

    private static StorageDrive Protected(string root, long totalGb, long usedGb, int start, int back, params StorageTitle[] titles) =>
        new()
        {
            Root = root,
            IsProtected = true,
            TotalBytes = totalGb * Gb,
            FreeBytes = (totalGb - usedGb) * Gb,
            LimitPercent = start,
            BringBackToPercent = back,
            Titles = titles.ToList()
        };

    private static StorageDrive Overflow(string root, long totalGb, long usedGb, long? keepFreeGb = null, params StorageTitle[] titles) =>
        new()
        {
            Root = root,
            TotalBytes = totalGb * Gb,
            FreeBytes = (totalGb - usedGb) * Gb,
            KeepFreeSetBytes = keepFreeGb * Gb,
            Titles = titles.ToList()
        };

    private static readonly List<StoragePlanner.Destination> Everywhere =
    [
        new(@"C:\", "C", StorageKinds.Movies, @"C:\Movies"),
        new(@"C:\", "C", StorageKinds.Tv, @"C:\TV"),
        new(@"O:\", "O", StorageKinds.Movies, @"O:\Movies"),
        new(@"O:\", "O", StorageKinds.Tv, @"O:\TV"),
        new(@"F:\", "F", StorageKinds.Movies, @"F:\Movies"),
        new(@"H:\", "H", StorageKinds.Movies, @"H:\Movies")
    ];

    [TestMethod]
    public void A_protected_drive_under_its_start_gets_no_suggestions()
    {
        var drives = new List<StorageDrive>
        {
            Protected(@"C:\", 1000, 790, 80, 60, Title("Night Shift", 50)),
            Overflow(@"O:\", 1000, 100)
        };

        Assert.IsEmpty(StoragePlanner.Suggest(drives, Everywhere));
    }

    [TestMethod]
    public void Over_its_start_a_protected_drive_is_brought_back_to_its_target()
    {
        // 850 used: over 80%, so down to 60% (600): 250 GB has to go.
        var drives = new List<StorageDrive>
        {
            Protected(@"C:\", 1000, 850, 80, 60,
                Title("A", 100), Title("B", 100), Title("C", 60), Title("D", 30), Title("E", 10)),
            Overflow(@"O:\", 4000, 1000)
        };

        var suggestions = StoragePlanner.Suggest(drives, Everywhere);

        // Smallest first while nobody's watch history says otherwise.
        CollectionAssert.AreEqual(
            new[] { "E", "D", "C", "A", "B" },
            suggestions.Select(suggestion => suggestion.Title.Name).ToArray());
        Assert.IsGreaterThanOrEqualTo(250 * Gb, suggestions.Sum(suggestion => suggestion.Title.SizeBytes));
    }

    [TestMethod]
    public void Titles_go_to_the_overflow_drive_with_the_most_free_space_not_the_lowest_percentage()
    {
        // O is 80% full but has 2 TB free; F is 60% full with 400 GB free.
        var drives = new List<StorageDrive>
        {
            Protected(@"C:\", 1000, 850, 80, 60, Title("Night Shift", 40)),
            Overflow(@"O:\", 10000, 8000, keepFreeGb: 100),
            Overflow(@"F:\", 1000, 600, keepFreeGb: 100)
        };

        Assert.AreEqual(@"O:\", StoragePlanner.Suggest(drives, Everywhere).First().ToRoot);
    }

    [TestMethod]
    public void Nothing_is_ever_moved_onto_a_protected_drive()
    {
        var drives = new List<StorageDrive>
        {
            Protected(@"C:\", 1000, 900, 80, 60, Title("Night Shift", 40)),
            Protected(@"H:\", 10000, 100, 80, 60),
            Overflow(@"O:\", 1000, 500)
        };

        Assert.AreEqual(@"O:\", StoragePlanner.Suggest(drives, Everywhere).Single().ToRoot);
    }

    [TestMethod]
    public void An_overflow_drive_always_keeps_its_free_space()
    {
        // O keeps 100 GB free and has 150 GB free: 50 GB of room.
        var drives = new List<StorageDrive>
        {
            Protected(@"C:\", 1000, 900, 80, 60, Title("Huge", 70), Title("Fits", 40), Title("AlsoFits", 30)),
            Overflow(@"O:\", 1000, 850, keepFreeGb: 100)
        };

        var suggestions = StoragePlanner.Suggest(drives, Everywhere);

        // AlsoFits (30) goes first, then Fits (40) no longer fits, nor Huge.
        Assert.AreEqual("AlsoFits", suggestions.Single().Title.Name);
    }

    [TestMethod]
    public void An_overflow_drive_keeps_a_tenth_free_when_not_set()
    {
        var drive = Overflow(@"O:\", 1000, 950);

        Assert.AreEqual(100 * Gb, drive.KeepFreeBytes);
        Assert.IsTrue(drive.IsOverLimit);
        Assert.AreEqual(50 * Gb, drive.ToFreeBytes);
    }

    [TestMethod]
    public void The_least_watched_titles_leave_first_and_favorites_last()
    {
        var popular = Title("Popular", 10);
        var forgotten = Title("Forgotten", 30);
        var favorite = Title("Favorite", 5);
        var unwatched = Title("Unwatched", 20);

        var watch = new Dictionary<StorageTitle, WatchRecord>
        {
            [popular] = new() { StartedBy = ["a", "b", "c"], LastWatchedUtc = new DateTime(2026, 11, 1) },
            [forgotten] = new() { StartedBy = ["a"], LastWatchedUtc = new DateTime(2025, 1, 1) },
            [favorite] = new() { FavoriteOf = ["a"] }
        };

        var order =
            StoragePlanner.MoveOrder([popular, forgotten, favorite, unwatched], title => watch.GetValueOrDefault(title));

        CollectionAssert.AreEqual(
            new[] { "Unwatched", "Forgotten", "Popular", "Favorite" },
            order.Select(title => title.Name).ToArray());
    }

    [TestMethod]
    public void Titles_over_the_largest_size_are_left_for_you()
    {
        var drives = new List<StorageDrive>
        {
            Protected(@"C:\", 1000, 900, 80, 60, Title("Giant", 150), Title("Small", 20)),
            Overflow(@"O:\", 4000, 100)
        };

        var suggestions = StoragePlanner.Suggest(drives, Everywhere, largestBytes: 100 * Gb);

        Assert.AreEqual("Small", suggestions.Single().Title.Name);
        var held = StoragePlanner.HeldBack(drives, null, 100 * Gb, DateTime.MaxValue).Single();

        Assert.AreEqual("Giant", held.Title.Name);
        Assert.AreEqual("Too big to move by itself", held.Reason);
    }

    [TestMethod]
    public void Titles_added_or_changed_recently_stay_put()
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var cutoff = now.AddDays(-14);

        var newEpisode = Title("Still Airing", 10, StorageKinds.Tv);
        newEpisode.LastWriteUtc = now.AddDays(-2);

        var justAdded = Title("Just Added", 10);
        justAdded.LastWriteUtc = now.AddYears(-3);

        var old = Title("Old", 10);
        old.LastWriteUtc = now.AddYears(-1);

        var drives = new List<StorageDrive>
        {
            Protected(@"C:\", 1000, 900, 80, 60, newEpisode, justAdded, old),
            Overflow(@"O:\", 4000, 100)
        };

        // Jellyfin added it last week, from a file written long ago.
        WatchRecord? Watch(StorageTitle title) =>
            title == justAdded ? new WatchRecord { AddedUtc = now.AddDays(-7) } : null;

        var suggestions = StoragePlanner.Suggest(drives, Everywhere, Watch, recentAfterUtc: cutoff);

        Assert.AreEqual("Old", suggestions.Single().Title.Name);
        CollectionAssert.AreEquivalent(
            new[] { "Still Airing", "Just Added" },
            StoragePlanner.HeldBack(drives, Watch, long.MaxValue, cutoff).Select(entry => entry.Title.Name).ToArray());
    }

    [TestMethod]
    public void New_artwork_or_an_nfo_doesnt_make_an_old_movie_recent()
    {
        var library = Path.Combine(Path.GetTempPath(), "storage-recent");
        var old = new DateTime(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);
        var today = new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);

        var files = new List<FileRecord>
        {
            new() { FullPath = Path.Combine(library, "Replicants (1982)", "film.mkv"), SizeBytes = 100, LastModifiedUtc = old },
            new() { FullPath = Path.Combine(library, "Replicants (1982)", "poster.jpg"), SizeBytes = 5, LastModifiedUtc = today },
            new() { FullPath = Path.Combine(library, "Replicants (1982)", "movie.nfo"), SizeBytes = 5, LastModifiedUtc = today },
            new() { FullPath = Path.Combine(library, "New Film (2026)", "film.mkv"), SizeBytes = 100, LastModifiedUtc = today }
        };

        var titles = StorageService.Measure("D", StorageKinds.Movies, library, files, []);
        var cutoff = today.AddDays(-14);

        Assert.IsFalse(StoragePlanner.IsRecent(titles.Single(title => title.Name == "Replicants (1982)"), null, cutoff));
        Assert.IsTrue(StoragePlanner.IsRecent(titles.Single(title => title.Name == "New Film (2026)"), null, cutoff));
    }

    [TestMethod]
    public void Seeding_titles_and_books_are_never_suggested()
    {
        var drives = new List<StorageDrive>
        {
            Protected(@"C:\", 1000, 900, 75, 65,
                Title("Seeding", 200, seedingGb: 150),
                Title("A Book", 200, StorageKinds.Books),
                Title("Movable", 20)),
            Overflow(@"O:\", 1000, 100)
        };

        var suggestions = StoragePlanner.Suggest(drives, Everywhere);

        Assert.AreEqual("Movable", suggestions.Single().Title.Name);
    }

    [TestMethod]
    public void TV_only_goes_to_a_drive_with_a_TV_library()
    {
        var drives = new List<StorageDrive>
        {
            Protected(@"C:\", 1000, 800, 75, 65, Title("Harbour", 80, StorageKinds.Tv)),
            Overflow(@"H:\", 1000, 100),
            Overflow(@"O:\", 1000, 600)
        };

        Assert.AreEqual(@"O:\", StoragePlanner.Suggest(drives, Everywhere).Single().ToRoot);
    }

    [TestMethod]
    public void Titles_are_one_per_top_folder()
    {
        var library = Path.Combine(Path.GetTempPath(), "storage-measure");

        var files = new List<FileRecord>
        {
            new() { FullPath = Path.Combine(library, "Night Shift (2021)", "film.mkv"), SizeBytes = 100, LastModifiedUtc = new DateTime(2025, 1, 1) },
            new() { FullPath = Path.Combine(library, "Night Shift (2021)", "film.srt"), SizeBytes = 5, LastModifiedUtc = new DateTime(2025, 3, 1) },
            new() { FullPath = Path.Combine(library, "Harbour", "Season 1", "e1.mkv"), SizeBytes = 40 },
            new() { FullPath = Path.Combine(library, "Harbour", "Season 2", "e1.mkv"), SizeBytes = 60 },
            new() { FullPath = Path.Combine(library, "loose.mkv"), SizeBytes = 7 }
        };

        var titles = StorageService.Measure("D", StorageKinds.Movies, library, files, []);

        Assert.HasCount(3, titles);

        var night = titles.Single(title => title.Name == "Night Shift (2021)");

        Assert.AreEqual(105, night.SizeBytes);
        Assert.AreEqual(2, night.FileCount);
        Assert.AreEqual(new DateTime(2025, 3, 1), night.LastWriteUtc);
        Assert.AreEqual(100, titles.Single(title => title.Name == "Harbour").SizeBytes);
        Assert.IsFalse(night.IsSeeding);
    }

    [TestMethod]
    public void A_library_file_linked_into_downloads_counts_as_seeding()
    {
        if (!NativeHardlinks.IsSupported)
        {
            Assert.Inconclusive("Hardlinks are Windows-only here.");
        }

        var root = Path.Combine(Path.GetTempPath(), "storage-seed-" + Guid.NewGuid().ToString("N"));
        var library = Path.Combine(root, "Movies");
        var downloads = Path.Combine(root, "Torrents");

        try
        {
            Directory.CreateDirectory(Path.Combine(library, "Harbour"));
            Directory.CreateDirectory(downloads);

            var libraryFile = Path.Combine(library, "Harbour", "Harbour.mkv");

            using (var stream = File.Create(libraryFile))
            {
                stream.SetLength(60L * 1024 * 1024);
            }

            Assert.IsNull(NativeHardlinks.CreateHardlink(libraryFile, Path.Combine(downloads, "Harbour.mkv")));

            var files = new List<FileRecord> { new() { FullPath = libraryFile, SizeBytes = 60L * 1024 * 1024 } };

            var title = StorageService.Measure("D", StorageKinds.Movies, library, files, [downloads]).Single();

            Assert.IsTrue(title.IsSeeding);
            Assert.IsFalse(title.CanMove);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
