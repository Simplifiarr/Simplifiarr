using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class DuplicateAssetAnalysisTests : TempFolderTest
{
    private readonly DuplicateAssetAnalysisService _service = new(new HardlinkService());

    private DriveProfile Profile() =>
        new()
        {
            Name = "D",
            DownloadPath = Path.Combine(Folder, "downloads"),
            MovieLibraryPath = Path.Combine(Folder, "movies"),
            TvLibraryPath = Path.Combine(Folder, "tv")
        };

    private MediaAsset Movie(string relativePath, string title, int year, string content) =>
        new()
        {
            MediaType = MediaType.Movie,
            Title = title,
            Year = year,
            CanonicalPath = WriteFile(relativePath, content),
            FileSizeBytes = content.Length
        };

    [TestMethod]
    public async Task Two_copies_of_one_film_are_a_duplicate_with_the_largest_first()
    {
        var small = Movie(@"movies\Charade 720p\Charade.720p.mkv", "Charade", 1963, "small");
        var large = Movie(@"movies\Charade\Charade.1080p.mkv", "Charade", 1963, "much larger file");
        var other = Movie(@"movies\Metropolis\Metropolis.mkv", "Metropolis", 1927, "other");

        var groups = await _service.FindDuplicatesAsync([small, large, other], [Profile()]);

        Assert.HasCount(1, groups);
        Assert.AreEqual("Charade", groups[0].Title);
        Assert.AreEqual(large.CanonicalPath, groups[0].Entries[0].CanonicalPath);
        Assert.AreEqual("D", groups[0].Entries[0].DriveProfileName);
        Assert.AreEqual(small.FileSizeBytes, groups[0].ReclaimableSizeBytes);
    }

    [TestMethod]
    public async Task The_same_title_from_a_different_year_is_not_a_duplicate()
    {
        var original = Movie(@"movies\a\a.mkv", "Nosferatu", 1922, "one");
        var remake = Movie(@"movies\b\b.mkv", "Nosferatu", 1979, "two");

        Assert.IsEmpty(await _service.FindDuplicatesAsync([original, remake], [Profile()]));
    }

    [TestMethod]
    public async Task Hardlinks_of_one_file_are_not_duplicates()
    {
        var library = Movie(@"movies\Charade\Charade.mkv", "Charade", 1963, "bytes");
        var linkedPath = Path.Combine(Folder, "movies", "Collection", "Charade.mkv");

        var linked = await new HardlinkService().CreateHardlinkAsync(library.CanonicalPath, linkedPath);

        Assert.IsTrue(linked.Success, linked.ErrorMessage);

        var second = new MediaAsset
        {
            MediaType = MediaType.Movie,
            Title = "Charade",
            Year = 1963,
            CanonicalPath = linkedPath,
            FileSizeBytes = 5
        };

        Assert.IsEmpty(await _service.FindDuplicatesAsync([library, second], [Profile()]));
    }

    [TestMethod]
    public async Task A_file_in_the_downloads_folder_is_not_counted_as_a_library_copy()
    {
        // What happens when a library folder is set too wide and the
        // catalog picks up downloads as if they were library files.
        var library = Movie(@"movies\Charade\Charade.mkv", "Charade", 1963, "library copy");
        var download = Movie(@"downloads\Charade.1963.1080p.mkv", "Charade", 1963, "download");

        Assert.IsEmpty(await _service.FindDuplicatesAsync([library, download], [Profile()]));
    }

    [TestMethod]
    public async Task A_copy_that_has_been_removed_no_longer_makes_a_duplicate()
    {
        var kept = Movie(@"movies\Charade\Charade.1080p.mkv", "Charade", 1963, "kept");
        var removed = Movie(@"movies\Charade 720p\Charade.720p.mkv", "Charade", 1963, "gone");

        File.Delete(removed.CanonicalPath);

        Assert.IsEmpty(await _service.FindDuplicatesAsync([kept, removed], [Profile()]));
    }

    [TestMethod]
    public async Task Episodes_are_matched_by_show_season_and_episode()
    {
        MediaAsset Episode(string path, int episode, string content) =>
            new()
            {
                MediaType = MediaType.TvEpisode,
                Title = "Some Show",
                Season = 1,
                Episode = episode,
                CanonicalPath = WriteFile(path, content),
                FileSizeBytes = content.Length
            };

        var groups =
            await _service.FindDuplicatesAsync(
                [
                    Episode(@"tv\Some Show\S01E01.1080p.mkv", 1, "big copy"),
                    Episode(@"tv\Some Show old\S01E01.720p.mkv", 1, "small"),
                    Episode(@"tv\Some Show\S01E02.1080p.mkv", 2, "only one")
                ],
                [Profile()]);

        Assert.HasCount(1, groups);
        Assert.AreEqual(1, groups[0].Episode);
        Assert.HasCount(2, groups[0].Entries);
    }

    [TestMethod]
    public async Task A_title_whose_other_copy_is_marked_not_a_duplicate_is_dropped()
    {
        var kept = Movie(@"movies\Film (2020)\Film.1080p.mkv", "Film", 2020, "a large copy");
        var ignored = Movie(@"movies\Film (2020) Extended\Film.720p.mkv", "Film", 2020, "small");
        var other1 = Movie(@"movies\Other (2019)\Other.1080p.mkv", "Other", 2019, "a large copy");
        var other2 = Movie(@"movies\Other (2019) 4K\Other.2160p.mkv", "Other", 2019, "an even larger copy");

        var groups = DuplicateAssetAnalysisService.RemoveIgnored(
            await _service.FindDuplicatesAsync([kept, ignored, other1, other2], [Profile()]),
            new IgnoreOnly(ignored.CanonicalPath));

        Assert.HasCount(1, groups);
        Assert.AreEqual("Other", groups[0].Title);
    }

    [TestMethod]
    public async Task The_same_episode_named_differently_on_two_drives_is_a_duplicate()
    {
        // Sonarr's name on one drive, the file's own name on another.
        var first = Episode(@"tv\Night Shift - Origins\S01E01.mkv", "Night Shift: Origins", "first drive copy");
        var second = Episode(@"other\Night Shift - Origins\S01E01.mkv", "Night Shift Origins", "second copy");

        var group = (await _service.FindDuplicatesAsync([first, second], [Profile()])).Single();

        Assert.HasCount(2, group.Entries);
    }

    [TestMethod]
    public void Titles_compare_by_letters_and_numbers_only()
    {
        Assert.AreEqual(
            DuplicateAssetAnalysisService.NormalizeTitle("The Bakers Dozen"),
            DuplicateAssetAnalysisService.NormalizeTitle("The Baker's Dozen"));
        Assert.AreEqual(
            DuplicateAssetAnalysisService.NormalizeTitle("Don’t Wake the Lighthouse"),
            DuplicateAssetAnalysisService.NormalizeTitle("Don't wake the lighthouse"));
        Assert.AreEqual(
            DuplicateAssetAnalysisService.NormalizeTitle("Salt and Pepper"),
            DuplicateAssetAnalysisService.NormalizeTitle("Salt & Pepper"));
        Assert.AreNotEqual(
            DuplicateAssetAnalysisService.NormalizeTitle("Harbour"),
            DuplicateAssetAnalysisService.NormalizeTitle("Harbours"));
    }

    private MediaAsset Episode(string relativePath, string title, string content) =>
        new()
        {
            MediaType = MediaType.TvEpisode,
            Title = title,
            Season = 1,
            Episode = 1,
            CanonicalPath = WriteFile(relativePath, content),
            FileSizeBytes = content.Length
        };

    private sealed class IgnoreOnly(string path) : IIgnoredItemsService
    {
        public IReadOnlyList<IgnoredItem> GetAll() => [];
        public bool IsIgnored(string sourcePath) => string.Equals(sourcePath, path, StringComparison.OrdinalIgnoreCase);
        public void Add(IgnoredItem item) { }
        public void AddRange(IEnumerable<IgnoredItem> items) { }
        public void Remove(string sourcePath) { }
        public int RemoveRange(IEnumerable<string> sourcePaths) => 0;
    }
}
