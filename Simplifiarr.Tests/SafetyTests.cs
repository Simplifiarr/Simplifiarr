using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class DriveProfileValidatorTests
{
    [TestMethod]
    public void A_normal_setup_has_no_problems()
    {
        var profile = new DriveProfile
        {
            DownloadPath = @"D:\Torrents",
            SeedOnlyPath = @"D:\Torrents\SeedOnly",
            MovieLibraryPath = @"D:\Movies",
            TvLibraryPath = @"D:\TV",
            BookLibraryPath = @"D:\Books",
            AudiobookLibraryPath = @"D:\Audiobooks",
            AudiobookDownloadPath = @"D:\Torrents\Audiobooks",
            MusicLibraryPath = @"D:\Music",
            MusicDownloadPath = @"D:\Torrents\Music",
            ComicLibraryPath = @"D:\Comics",
            ComicDownloadPath = @"D:\Torrents\Comics"
        };

        Assert.IsEmpty(DriveProfileValidator.GetProblems(profile));
    }

    [TestMethod]
    public void An_empty_profile_has_no_problems()
    {
        Assert.IsEmpty(DriveProfileValidator.GetProblems(new DriveProfile()));
    }

    [TestMethod]
    public void A_library_set_to_the_whole_drive_is_flagged_once_per_kind_of_problem()
    {
        var profile = new DriveProfile
        {
            DownloadPath = @"f:\torrents\",
            MovieLibraryPath = @"f:\",
            TvLibraryPath = @"F:\TV",
            BookLibraryPath = @"F:\Books",
            AudiobookDownloadPath = @"F:\Torrents\Audiobooks"
        };

        var problems = DriveProfileValidator.GetProblems(profile);

        Assert.HasCount(2, problems);
        StringAssert.Contains(problems[0], "Movie library");
        StringAssert.Contains(problems[0], "contains the Downloads folder");
        StringAssert.Contains(problems[1], "contains the TV library and Book library");
    }

    [TestMethod]
    public void A_library_inside_the_downloads_folder_is_flagged()
    {
        var problems =
            DriveProfileValidator.GetProblems(
                new DriveProfile
                {
                    DownloadPath = @"H:\Torrents",
                    ComicLibraryPath = @"H:\Torrents\Comics",
                    ComicDownloadPath = @"H:\Torrents"
                });

        Assert.HasCount(1, problems);
        StringAssert.Contains(problems[0], "Comic library");
        StringAssert.Contains(problems[0], "is inside the Downloads folder");
    }

    [TestMethod]
    public void A_library_that_is_the_downloads_folder_is_flagged()
    {
        var problems =
            DriveProfileValidator.GetProblems(
                new DriveProfile
                {
                    DownloadPath = @"D:\Media",
                    MovieLibraryPath = @"d:\media\"
                });

        Assert.HasCount(1, problems);
        StringAssert.Contains(problems[0], "are the same folder");
    }

    [TestMethod]
    public void Two_libraries_sharing_a_folder_are_flagged_once()
    {
        var problems =
            DriveProfileValidator.GetProblems(
                new DriveProfile
                {
                    MovieLibraryPath = @"D:\Video",
                    TvLibraryPath = @"D:\Video"
                });

        Assert.HasCount(1, problems);
        StringAssert.Contains(problems[0], "Movie library and TV library are the same folder");
    }

    [TestMethod]
    public void A_folder_whose_name_only_starts_the_same_is_not_a_problem()
    {
        Assert.IsEmpty(
            DriveProfileValidator.GetProblems(
                new DriveProfile
                {
                    DownloadPath = @"D:\Torrents",
                    MovieLibraryPath = @"D:\Torrents-Library"
                }));
    }
}

[TestClass]
public class FullCopyDetectionTests : TempFolderTest
{
    private DuplicateCandidateService Service() =>
        new(
            new FileInventoryCacheService(new FileScannerService()),
            new HardlinkService());

    [TestMethod]
    public async Task A_library_file_with_a_same_sized_download_is_a_candidate()
    {
        WriteFile(@"movies\Movie (2020)\Movie (2020).mkv", "same-bytes");
        WriteFile(@"downloads\Movie.2020.1080p.mkv", "same-bytes");
        WriteFile(@"downloads\Other.2021.1080p.mkv", "different length");

        var found =
            await Service().FindVideoCandidatesAsync(
                Path.Combine(Folder, "movies"),
                Path.Combine(Folder, "downloads"),
                forceRefresh: true);

        Assert.HasCount(1, found);
        Assert.IsFalse(found[0].IsAlreadyHardlinked);
        StringAssert.EndsWith(found[0].LibraryPath, "Movie (2020).mkv");
        StringAssert.EndsWith(found[0].DownloadPath, "Movie.2020.1080p.mkv");
    }

    [TestMethod]
    public async Task When_the_library_folder_contains_the_downloads_no_file_is_paired_with_itself()
    {
        // The setup that produced "full copies" of files that were really
        // one file: the movie library is the whole drive.
        WriteFile(@"downloads\Movie.2020.1080p.mkv", "same-bytes");
        WriteFile(@"downloads\Other.2021.1080p.mkv", "different length");

        var found =
            await Service().FindVideoCandidatesAsync(
                Folder,
                Path.Combine(Folder, "downloads"),
                forceRefresh: true);

        Assert.IsEmpty(found);
    }

    [TestMethod]
    public async Task Real_copies_are_still_found_when_the_library_contains_the_downloads()
    {
        WriteFile(@"Movie (2020)\Movie (2020).mkv", "same-bytes");
        WriteFile(@"downloads\Movie.2020.1080p.mkv", "same-bytes");

        var found =
            await Service().FindVideoCandidatesAsync(
                Folder,
                Path.Combine(Folder, "downloads"),
                forceRefresh: true);

        Assert.HasCount(1, found);
        StringAssert.EndsWith(found[0].LibraryPath, @"Movie (2020)\Movie (2020).mkv");
    }

    [TestMethod]
    public async Task A_plan_to_convert_a_file_into_itself_is_refused()
    {
        var file = WriteFile(@"downloads\Movie.2020.1080p.mkv", "bytes");

        var plan =
            await new HardlinkConversionPlanner().CreatePlanAsync(
                new DuplicateCandidate
                {
                    LibraryPath = file,
                    DownloadPath = file.ToUpperInvariant(),
                    LibraryFileName = "Movie.2020.1080p.mkv",
                    SizeBytes = 5,
                    LibraryHardlinkCount = 1
                });

        Assert.IsFalse(plan!.CanConvert);
        StringAssert.Contains(plan.Reason, "same file");
    }

    [TestMethod]
    public async Task Converting_refuses_to_touch_a_file_that_is_already_the_download()
    {
        var file = WriteFile(@"downloads\Movie.2020.1080p.mkv", "bytes");
        var settings = new FixedSettingsService();
        var history = new HistoryService(settings);

        var remediation =
            new HardlinkRemediationService(
                new HardlinkService(),
                new QuarantineService(settings),
                history);

        // A plan that wrongly says "go ahead" - as an old cached one could.
        var converted =
            await remediation.ExecuteAsync(
                new HardlinkConversionPlan
                {
                    LibraryPath = file,
                    DownloadPath = file,
                    CanConvert = true
                });

        Assert.IsFalse(converted);
        Assert.IsTrue(File.Exists(file), "the file must still be where it was");
        Assert.AreEqual("bytes", File.ReadAllText(file));
    }
}
