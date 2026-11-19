using System.Text.Json;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class DownloadFolderTests
{
    private static DriveProfile Split() =>
        new()
        {
            Name = "D",
            DownloadPath = @"D:\Torrents",
            MovieDownloadPath = @"D:\Torrents\Movies",
            TvDownloadPath = @"D:\Torrents\TV",
            BookDownloadPath = @"D:\Torrents\Books",
            MusicDownloadPath = @"D:\Torrents\Music"
        };

    [TestMethod]
    public void Blank_kind_folders_fall_back_to_the_main_one()
    {
        var profile = new DriveProfile { DownloadPath = @"D:\Torrents" };

        Assert.AreEqual(@"D:\Torrents", profile.MovieDownloads);
        Assert.AreEqual(@"D:\Torrents", profile.TvDownloads);
        Assert.AreEqual(@"D:\Torrents", profile.BookDownloads);
        Assert.AreEqual(@"D:\Torrents", MediaTypeDefinitions.Book.GetDownloadPath(profile));
        CollectionAssert.AreEqual(new[] { @"D:\Torrents" }, profile.AllDownloadPaths());
    }

    [TestMethod]
    public void Every_downloads_folder_is_listed_once()
    {
        CollectionAssert.AreEquivalent(
            new[] { @"D:\Torrents", @"D:\Torrents\Movies", @"D:\Torrents\TV", @"D:\Torrents\Books", @"D:\Torrents\Music" },
            Split().AllDownloadPaths());

        Assert.AreEqual(@"D:\Torrents\Books", MediaTypeDefinitions.Book.GetDownloadPath(Split()));
    }

    [TestMethod]
    public void A_file_in_a_movie_or_TV_folder_is_only_that_kind()
    {
        var split = Split();

        Assert.AreEqual(MediaKind.Movies, MoviePromotionCandidateService.FolderKind(split, @"D:\Torrents\Movies\Night Shift (2021)\a.mkv"));
        Assert.AreEqual(MediaKind.Tv, MoviePromotionCandidateService.FolderKind(split, @"D:\Torrents\TV\Harbour S01\e1.mkv"));

        // Shared main folder: either kind.
        Assert.IsNull(MoviePromotionCandidateService.FolderKind(new DriveProfile { DownloadPath = @"D:\Torrents" }, @"D:\Torrents\a.mkv"));

        // Only movies split off: the main folder is then TV's.
        var moviesOnly = new DriveProfile { DownloadPath = @"D:\Torrents", MovieDownloadPath = @"D:\Torrents\Movies" };

        Assert.AreEqual(MediaKind.Movies, MoviePromotionCandidateService.FolderKind(moviesOnly, @"D:\Torrents\Movies\a.mkv"));
        Assert.AreEqual(MediaKind.Tv, MoviePromotionCandidateService.FolderKind(moviesOnly, @"D:\Torrents\other\a.mkv"));
    }

    [TestMethod]
    public void A_kind_folder_outside_the_main_one_still_belongs_to_its_drive()
    {
        var elsewhere = new DriveProfile { Name = "E", DownloadPath = @"E:\Torrents", TvDownloadPath = @"E:\Shows-Incoming" };
        var other = new DriveProfile { Name = "D", DownloadPath = @"D:\Torrents" };

        Assert.AreEqual("E", MoviePromotionCandidateService.OwningProfile([other, elsewhere], @"E:\Shows-Incoming\x.mkv")?.Name);
        Assert.IsNull(MoviePromotionCandidateService.OwningProfile([other, elsewhere], @"E:\Somewhere\x.mkv"));
    }

    [TestMethod]
    public void A_library_on_a_kind_downloads_folder_is_reported()
    {
        var profile = Split();
        profile.TvLibraryPath = @"D:\Torrents\TV";

        Assert.IsTrue(DriveProfileValidator.GetProblems(profile).Any(problem => problem.Contains("TV downloads folder")));
    }

    [TestMethod]
    public void Saved_settings_keep_the_folders_but_not_the_fallbacks()
    {
        var json = JsonSerializer.Serialize(Split());

        Assert.Contains("\"MovieDownloadPath\"", json);
        Assert.DoesNotContain("\"MovieDownloads\"", json);

        var old = JsonSerializer.Deserialize<DriveProfile>("""{ "DownloadPath": "D:\\Torrents" }""")!;

        Assert.AreEqual(string.Empty, old.MovieDownloadPath);
        Assert.AreEqual(@"D:\Torrents", old.MovieDownloads);
    }
}
