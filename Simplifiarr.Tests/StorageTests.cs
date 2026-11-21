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

    private static StorageDrive Drive(string root, long totalGb, long usedGb, int limit, params StorageTitle[] titles) =>
        new()
        {
            Root = root,
            TotalBytes = totalGb * Gb,
            FreeBytes = (totalGb - usedGb) * Gb,
            LimitPercent = limit,
            Titles = titles.ToList()
        };

    private static readonly List<StoragePlanner.Destination> Everywhere =
    [
        new(@"C:\", "C", StorageKinds.Movies, @"C:\Movies"),
        new(@"C:\", "C", StorageKinds.Tv, @"C:\TV"),
        new(@"O:\", "O", StorageKinds.Movies, @"O:\Movies"),
        new(@"O:\", "O", StorageKinds.Tv, @"O:\TV"),
        new(@"H:\", "H", StorageKinds.Movies, @"H:\Movies")
    ];

    [TestMethod]
    public void A_drive_under_its_limit_gets_no_suggestions()
    {
        var drives = new List<StorageDrive>
        {
            Drive(@"C:\", 1000, 700, 75, Title("Night Shift", 50)),
            Drive(@"O:\", 1000, 100, 90)
        };

        Assert.IsEmpty(StoragePlanner.Suggest(drives, Everywhere));
    }

    [TestMethod]
    public void The_fewest_largest_titles_go_to_the_drive_with_most_room()
    {
        // 800 used of a 75% limit (750): 50 GB has to go.
        var drives = new List<StorageDrive>
        {
            Drive(@"C:\", 1000, 800, 75, Title("Small", 10), Title("Big", 60), Title("Medium", 30)),
            Drive(@"O:\", 1000, 300, 90),
            Drive(@"H:\", 1000, 800, 90)
        };

        var suggestion = StoragePlanner.Suggest(drives, Everywhere).Single();

        Assert.AreEqual("Big", suggestion.Title.Name);
        Assert.AreEqual(@"O:\", suggestion.ToRoot);
        Assert.AreEqual(@"O:\Movies", suggestion.ToLibraryPath);
    }

    [TestMethod]
    public void Seeding_titles_and_books_are_never_suggested()
    {
        var drives = new List<StorageDrive>
        {
            Drive(@"C:\", 1000, 900, 75,
                Title("Seeding", 200, seedingGb: 150),
                Title("A Book", 200, StorageKinds.Books),
                Title("Movable", 20)),
            Drive(@"O:\", 1000, 100, 90)
        };

        var suggestions = StoragePlanner.Suggest(drives, Everywhere);

        Assert.AreEqual("Movable", suggestions.Single().Title.Name);
    }

    [TestMethod]
    public void A_move_never_pushes_the_other_drive_over_its_own_limit()
    {
        var drives = new List<StorageDrive>
        {
            Drive(@"C:\", 1000, 800, 75, Title("Huge", 100), Title("Fits", 40), Title("AlsoFits", 30)),
            Drive(@"O:\", 1000, 850, 90),
            Drive(@"H:\", 1000, 900, 90)
        };

        var suggestions = StoragePlanner.Suggest(drives, Everywhere);

        // O has 50 GB of room under its limit: Huge doesn't fit, Fits does,
        // and then AlsoFits no longer does.
        Assert.AreEqual("Fits", suggestions.Single().Title.Name);
    }

    [TestMethod]
    public void TV_only_goes_to_a_drive_with_a_TV_library()
    {
        var drives = new List<StorageDrive>
        {
            Drive(@"C:\", 1000, 800, 75, Title("Harbour", 80, StorageKinds.Tv)),
            Drive(@"H:\", 1000, 100, 90),
            Drive(@"O:\", 1000, 600, 90)
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
