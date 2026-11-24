using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

[TestClass]
public class OtherReleaseTests : TempFolderTest
{
    private FakeTorrentClient _client = default!;
    private FakeQuarantine _quarantine = default!;
    private DownloadCleanupService _service = default!;
    private string _downloads = default!;
    private string _movies = default!;
    private string _tv = default!;

    [TestInitialize]
    public void Build()
    {
        _downloads = Path.Combine(Folder, "c", "Torrents", "Movies");
        _movies = Path.Combine(Folder, "o", "Movies");
        _tv = Path.Combine(Folder, "o", "TV");

        foreach (var folder in new[] { _downloads, _movies, _tv })
        {
            Directory.CreateDirectory(folder);
        }

        var settings =
            new DriveSettings(
                new SimplifiarrSettings
                {
                    DriveProfiles =
                    [
                        new DriveProfile { Name = "C", DownloadPath = Path.Combine(Folder, "c", "Torrents"), MovieLibraryPath = Path.Combine(Folder, "c", "Movies") },
                        new DriveProfile { Name = "O", DownloadPath = Path.Combine(Folder, "o", "Torrents"), MovieLibraryPath = _movies, TvLibraryPath = _tv }
                    ]
                });

        Directory.CreateDirectory(Path.Combine(Folder, "c", "Movies"));
        Directory.CreateDirectory(Path.Combine(Folder, "o", "Torrents"));

        _client = new FakeTorrentClient();
        _quarantine = new FakeQuarantine();

        _service =
            new DownloadCleanupService(
                settings,
                new FileInventoryCacheService(new FileScannerService()),
                _client,
                new HardlinkService(),
                new HistoryService(settings),
                new ReleaseParser(),
                _quarantine)
            {
                // The test files are tiny.
                OtherReleaseMinimumBytes = 1
            };
    }

    private static string Write(string folder, string relative, string content)
    {
        var path = Path.Combine(folder, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return path;
    }

    [TestMethod]
    public async Task A_720p_leftover_of_a_movie_the_library_has_in_1080p_is_found()
    {
        var download = Write(_downloads, @"Night Pier (2021) [720p] [BluRay]\Night.Pier.2021.720p.BluRay.x264.AAC.mp4", "small old copy");
        var library = Write(_movies, @"Night Pier (2021)\Night.Pier.2021.1080p.BluRay.x264.AAC5.1.mp4", "bigger newer copy, different bytes");

        var candidate = (await _service.ScanAsync("C")).Candidates.Single();

        Assert.AreEqual(DownloadMatchKind.OtherRelease, candidate.MatchKind);
        Assert.AreEqual(download, candidate.DownloadPath);
        Assert.AreEqual(library, candidate.LibraryPath);
        Assert.AreEqual("720p", candidate.DownloadQuality);
        Assert.AreEqual("1080p", candidate.LibraryQuality);
    }

    [TestMethod]
    public async Task Another_movie_with_the_same_name_but_a_different_year_is_not_a_match()
    {
        Write(_downloads, @"Night.Pier.1984.720p.mkv", "the original");
        Write(_movies, @"Night Pier (2021)\Night Pier (2021).mkv", "the remake");

        Assert.IsEmpty((await _service.ScanAsync("C")).Candidates);
    }

    [TestMethod]
    public async Task An_episode_is_matched_by_show_season_and_episode()
    {
        Write(_downloads, @"Harbour.S02E05.720p.WEB.mkv", "old episode");
        Write(_tv, @"Harbour\Season 2\Harbour - S02E05 - The Gulls.mkv", "new episode, different bytes");
        Write(_tv, @"Harbour\Season 2\Harbour - S02E06 - Low Tide.mkv", "another episode");

        var candidate = (await _service.ScanAsync("C")).Candidates.Single();

        StringAssert.Contains(candidate.LibraryPath, "S02E05");
    }

    [TestMethod]
    public async Task A_release_a_torrent_still_uses_is_left_alone()
    {
        var download = Write(_downloads, @"Night.Pier.2021.720p.mkv", "small old copy");
        Write(_movies, @"Night Pier (2021)\Night Pier (2021).mkv", "bigger newer copy");

        _client.Torrents.Add(new TorrentRecord { ContentPath = download });

        Assert.IsEmpty((await _service.ScanAsync("C")).Candidates);
    }

    [TestMethod]
    public async Task Moving_one_sends_it_to_Quarantine_and_leaves_the_library_alone()
    {
        var download = Write(_downloads, @"Night.Pier.2021.720p.mkv", "small old copy");
        var library = Write(_movies, @"Night Pier (2021)\Night Pier (2021).mkv", "bigger newer copy");

        var candidate = (await _service.ScanAsync("C")).Candidates.Single();
        var result = await _service.RemoveAsync([candidate]);

        Assert.AreEqual(1, result.Removed);
        Assert.AreEqual(download, _quarantine.Files.Single());
        Assert.IsTrue(File.Exists(library));
    }

    [TestMethod]
    public void Identity_comes_from_the_name_or_the_movie_folder()
    {
        var parser = new ReleaseParser();

        var download = DownloadCleanupService.IdentityOf(parser, @"C:\T\Night.Pier.2021.720p.BluRay.x264.mp4");
        var library = DownloadCleanupService.IdentityOf(parser, @"O:\Movies\Night Pier (2021)\movie.mkv");

        Assert.IsNotNull(download);
        Assert.AreEqual(download, library);
        Assert.IsNull(DownloadCleanupService.IdentityOf(parser, @"C:\T\random clip.mkv"), "No year, no folder to say: not sure enough.");
    }
}
