using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class DownloadLeftoverTests : TempFolderTest
{
    private FakeTorrentClient _client = default!;
    private FakeQuarantine _quarantine = default!;
    private DownloadCleanupService _service = default!;
    private string _torrents = default!;
    private string _movies = default!;

    [TestInitialize]
    public void Build()
    {
        _torrents = Path.Combine(Folder, "c", "Torrents");
        _movies = Path.Combine(Folder, "o", "Movies");

        Directory.CreateDirectory(Path.Combine(_torrents, "Movies"));
        Directory.CreateDirectory(_movies);
        Directory.CreateDirectory(Path.Combine(Folder, "c", "Movies"));

        var settings =
            new DriveSettings(
                new SimplifiarrSettings
                {
                    DriveProfiles =
                    [
                        new DriveProfile { Name = "C", DownloadPath = _torrents, MovieLibraryPath = Path.Combine(Folder, "c", "Movies") },
                        new DriveProfile { Name = "O", MovieLibraryPath = _movies }
                    ]
                });

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

    private string Torrent(string name) => Path.Combine(_torrents, "Movies", name);

    [TestMethod]
    public async Task A_torrent_folder_is_one_leftover_and_goes_whole_with_its_junk()
    {
        var folder = Torrent("Night Pier (2021) [1080p]");
        Write(folder, "Night.Pier.2021.1080p.mkv", "the film");
        Write(folder, "Night.Pier.2021.1080p.srt", "subs");
        Write(folder, "Subs\\English.srt", "more subs");
        Write(folder, "www.example.txt", "advert");
        Write(_movies, @"Night Pier (2021)\Night Pier (2021).mkv", "the film");

        var leftover = (await _service.ScanAsync("C")).Leftovers.Single();

        Assert.AreEqual(folder, leftover.Path, "Grouped by torrent folder, past the Movies category folder.");
        Assert.AreEqual(DownloadLeftoverKind.IdenticalCopy, leftover.Kind);
        Assert.HasCount(3, leftover.Junk);
        Assert.IsTrue(leftover.RemovesFolder);

        var result = await _service.RemoveLeftoversAsync([leftover]);

        Assert.AreEqual(1, result.Removed, string.Join("; ", result.SkipReasons));
        Assert.IsFalse(Directory.Exists(folder), "Junk and folder are gone too.");
        Assert.IsTrue(File.Exists(Path.Combine(_movies, "Night Pier (2021)", "Night Pier (2021).mkv")));
    }

    [TestMethod]
    public async Task A_folder_left_with_only_junk_is_a_leftover_of_its_own()
    {
        var folder = Torrent("Old Download");
        Write(folder, "film.srt", "subs");
        Write(folder, "film.nfo", "info");
        Write(folder, "unfinished.mkv.!qB", "half a film");

        var leftover = (await _service.ScanAsync("C")).Leftovers.Single();

        Assert.AreEqual(DownloadLeftoverKind.LeftoversOnly, leftover.Kind);

        await _service.RemoveLeftoversAsync([leftover]);

        Assert.IsFalse(Directory.Exists(folder));
    }

    [TestMethod]
    public async Task A_folder_with_things_Simplifiarr_doesnt_know_is_never_junk()
    {
        var folder = Torrent("Some Game");
        Write(folder, "setup.exe", "a game");
        Write(folder, "data.doi", "game data");
        Write(folder, "readme.txt", "read me");

        Assert.IsEmpty((await _service.ScanAsync("C")).Leftovers);
    }

    [TestMethod]
    public async Task The_folder_stays_when_it_holds_something_unknown()
    {
        var folder = Torrent("Night Pier (2021)");
        Write(folder, "Night.Pier.2021.mkv", "the film");
        Write(folder, "extras.rar", "an archive");
        Write(_movies, @"Night Pier (2021)\Night Pier (2021).mkv", "the film");

        var leftover = (await _service.ScanAsync("C")).Leftovers.Single();

        Assert.IsFalse(leftover.RemovesFolder);

        await _service.RemoveLeftoversAsync([leftover]);

        Assert.IsTrue(File.Exists(Path.Combine(folder, "extras.rar")));
        Assert.IsFalse(File.Exists(Path.Combine(folder, "Night.Pier.2021.mkv")));
    }

    [TestMethod]
    public async Task A_folder_a_torrent_still_uses_is_not_listed_at_all()
    {
        var folder = Torrent("Night Pier (2021)");
        var film = Write(folder, "Night.Pier.2021.mkv", "the film");
        Write(folder, "film.nfo", "info");
        Write(_movies, @"Night Pier (2021)\Night Pier (2021).mkv", "the film");

        _client.Torrents.Add(new TorrentRecord { ContentPath = film });

        Assert.IsEmpty((await _service.ScanAsync("C")).Leftovers);
    }

    [TestMethod]
    public async Task A_download_the_library_doesnt_have_is_shown_but_never_removed()
    {
        var folder = Torrent("Never Imported (2020)");
        var film = Write(folder, "Never.Imported.2020.mkv", "a film");

        var leftover = (await _service.ScanAsync("C")).Leftovers.Single();

        Assert.AreEqual(DownloadLeftoverKind.NotInLibrary, leftover.Kind);

        var result = await _service.RemoveLeftoversAsync([leftover]);

        Assert.AreEqual(0, result.Removed);
        Assert.IsTrue(File.Exists(film));
    }

    [TestMethod]
    public async Task Another_release_goes_to_Quarantine_with_its_junk_deleted()
    {
        var folder = Torrent("Night Pier (2021) [720p]");
        var film = Write(folder, "Night.Pier.2021.720p.mkv", "small old copy");
        Write(folder, "Night.Pier.2021.720p.srt", "subs");
        Write(_movies, @"Night Pier (2021)\Night.Pier.2021.1080p.mkv", "newer bigger copy");

        var leftover = (await _service.ScanAsync("C")).Leftovers.Single();

        Assert.AreEqual(DownloadLeftoverKind.OtherRelease, leftover.Kind);

        await _service.RemoveLeftoversAsync([leftover]);

        Assert.AreEqual(film, _quarantine.Files.Single());
        Assert.IsFalse(Directory.Exists(folder));
    }
}
