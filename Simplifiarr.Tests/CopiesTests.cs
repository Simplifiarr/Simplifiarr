using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

[TestClass]
public class CopiesTests : TempFolderTest
{
    private FakeTorrentClient _client = default!;
    private FakeQuarantine _quarantine = default!;
    private CopiesService _service = default!;
    private string _torrents = default!;
    private string _movies = default!;
    private string _tv = default!;

    [TestInitialize]
    public void Build()
    {
        _torrents = Path.Combine(Folder, "c", "Torrents");
        _movies = Path.Combine(Folder, "c", "Movies");
        _tv = Path.Combine(Folder, "c", "TV");

        Directory.CreateDirectory(Path.Combine(_torrents, "Movies"));
        Directory.CreateDirectory(_movies);
        Directory.CreateDirectory(_tv);

        var settings =
            new DriveSettings(
                new SimplifiarrSettings
                {
                    DriveProfiles =
                    [
                        new DriveProfile { Name = "C", DownloadPath = _torrents, MovieLibraryPath = _movies, TvLibraryPath = _tv }
                    ]
                });

        _client = new FakeTorrentClient();
        _quarantine = new FakeQuarantine();

        var history = new HistoryService(settings);
        var hardlinks = new HardlinkService();
        var inventory = new FileInventoryCacheService(new FileScannerService());

        var downloads =
            new DownloadCleanupService(settings, inventory, _client, hardlinks, history, new ReleaseParser(), _quarantine);

        _service =
            new CopiesService(
                settings,
                inventory,
                _client,
                new ReleaseParser(),
                downloads,
                history,
                new HardlinkConversionPlanner(),
                new HardlinkRemediationService(hardlinks, _quarantine, history),
                _quarantine)
            {
                VersionMinimumBytes = 1,
                ListingMaxAge = TimeSpan.Zero
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

    private void Seeding(string path) =>
        _client.Torrents.Add(new TorrentRecord { Name = Path.GetFileName(path), ContentPath = path });

    [TestMethod]
    public async Task A_hardlinked_download_nothing_seeds_goes_with_its_folder()
    {
        var film = Write(_movies, @"Night Pier (2021)\Night Pier (2021).mkv", "the film");
        var folder = Torrent("Night.Pier.2021.1080p");
        Directory.CreateDirectory(folder);
        Assert.IsNull(NativeHardlinks.CreateHardlink(film, Path.Combine(folder, "Night.Pier.2021.1080p.mkv")));
        Write(folder, "Night.Pier.2021.1080p.nfo", "info");

        var result = await _service.ScanAsync();

        var leftover = result.ToDelete.Single();
        Assert.AreEqual(DownloadLeftoverKind.SameFile, leftover.Kind);
        Assert.IsEmpty(result.Versions, "One file under two names isn't two versions.");
        Assert.IsEmpty(result.ToLink);

        var done = await _service.DeleteAsync([leftover]);

        Assert.AreEqual(1, done.Done, string.Join("; ", done.Skipped));
        Assert.IsFalse(Directory.Exists(folder));
        Assert.IsTrue(File.Exists(film));
    }

    [TestMethod]
    public async Task The_same_bytes_seeding_on_the_same_drive_are_linked_not_deleted()
    {
        var film = Write(_movies, @"Night Pier (2021)\Night Pier (2021).mkv", "the film");
        var download = Write(Torrent("Night.Pier.2021.1080p"), "Night.Pier.2021.1080p.mkv", "the film");
        Seeding(Path.GetDirectoryName(download)!);

        var result = await _service.ScanAsync();

        Assert.IsEmpty(result.ToDelete, "A torrent still uses it.");
        Assert.IsEmpty(result.Versions);

        var link = result.ToLink.Single();
        var done = await _service.LinkAsync([link]);

        Assert.AreEqual(1, done.Done, string.Join("; ", done.Skipped));
        Assert.AreEqual(film, _quarantine.Files.Single(), "The library copy goes to Quarantine first.");
        Assert.AreEqual(NativeHardlinks.GetIdentity(download)!.Value.FileIndex, NativeHardlinks.GetIdentity(film)!.Value.FileIndex);
    }

    [TestMethod]
    public async Task The_same_bytes_nothing_seeds_are_deleted_after_a_byte_for_byte_check()
    {
        Write(_movies, @"Night Pier (2021)\Night Pier (2021).mkv", "the film");
        var download = Write(Torrent("Night.Pier.2021.1080p"), "Night.Pier.2021.1080p.mkv", "the film");

        var leftover = (await _service.ScanAsync()).ToDelete.Single();

        Assert.AreEqual(DownloadLeftoverKind.IdenticalCopy, leftover.Kind);

        // Same size and time, different inside: the check before deleting catches it.
        var time = File.GetLastWriteTimeUtc(download);
        File.WriteAllText(download, "the fiXm");
        File.SetLastWriteTimeUtc(download, time);

        var done = await _service.DeleteAsync([leftover]);

        Assert.AreEqual(0, done.Done);
        Assert.IsTrue(File.Exists(download));
    }

    [TestMethod]
    public async Task Another_release_is_a_version_and_goes_to_Quarantine_with_its_folder()
    {
        Write(_movies, @"Night Pier (2021)\Night Pier (2021) - 1080p.mkv", "the bigger newer film");
        var folder = Torrent("Night.Pier.2021.720p");
        var download = Write(folder, "Night.Pier.2021.720p.mkv", "older film");
        Write(folder, "Night.Pier.2021.720p.srt", "subs");

        var result = await _service.ScanAsync();

        Assert.IsEmpty(result.ToDelete, "Not the same file, so it isn't deleted - it's a version to choose.");

        var group = result.Versions.Single();
        Assert.AreEqual("movie|nightpier|2021", group.Identity);
        Assert.HasCount(2, group.Versions);
        Assert.Contains(download, result.MatchedDownloads);

        var version = group.Versions.Single(candidate => !candidate.InLibrary);
        Assert.AreEqual("720p", version.Quality);

        var done = await _service.RemoveVersionAsync(group, version);

        Assert.AreEqual(1, done.Done, string.Join("; ", done.Skipped) + done.Error);
        Assert.AreEqual(download, _quarantine.Files.Single());
        Assert.IsFalse(Directory.Exists(folder), "Its subtitles and folder go too.");
    }

    [TestMethod]
    public async Task A_seeding_version_is_shown_but_never_removed()
    {
        Write(_movies, @"Night Pier (2021)\Night Pier (2021).mkv", "the bigger newer film");
        var download = Write(Torrent("Night.Pier.2021.720p"), "Night.Pier.2021.720p.mkv", "older film");
        Seeding(download);

        var group = (await _service.ScanAsync()).Versions.Single();
        var version = group.Versions.Single(candidate => !candidate.InLibrary);

        Assert.IsTrue(version.Seeding);
        Assert.AreEqual(0, group.ReclaimableBytes);

        var done = await _service.RemoveVersionAsync(group, version);

        Assert.AreEqual(0, done.Done);
        Assert.IsTrue(File.Exists(download));
    }

    [TestMethod]
    public async Task A_version_whose_other_name_is_in_the_Recycle_Bin_still_goes_to_Quarantine()
    {
        // A swap sent the old library file (a hardlink of this download) to the Recycle Bin.
        Write(_movies, @"Night Pier (2021)\Night Pier (2021) x265.mkv", "the smaller swapped-in film");
        var download = Write(Torrent("Night.Pier.2021.1080p"), "Night.Pier.2021.1080p.mkv", "the bigger old film");
        var bin = Path.Combine(Folder, "$Recycle.Bin", "S-1-5-18");
        Directory.CreateDirectory(bin);
        Assert.IsNull(NativeHardlinks.CreateHardlink(download, Path.Combine(bin, "$RABC123.mkv")));

        var group = (await _service.ScanAsync()).Versions.Single();
        var done = await _service.RemoveVersionAsync(group, group.Versions.Single(version => !version.InLibrary));

        Assert.AreEqual(1, done.Done, string.Join("; ", done.Skipped) + done.Error);
        Assert.AreEqual(download, _quarantine.Files.Single());
        StringAssert.Contains(done.Note, "Recycle Bin");
    }

    [TestMethod]
    public async Task A_version_with_another_name_somewhere_else_is_left_alone()
    {
        Write(_movies, @"Night Pier (2021)\Night Pier (2021) x265.mkv", "the smaller film");
        var download = Write(Torrent("Night.Pier.2021.1080p"), "Night.Pier.2021.1080p.mkv", "the bigger film");
        var elsewhere = Path.Combine(Folder, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        Assert.IsNull(NativeHardlinks.CreateHardlink(download, Path.Combine(elsewhere, "kept.mkv")));

        var group = (await _service.ScanAsync()).Versions.Single();
        var done = await _service.RemoveVersionAsync(group, group.Versions.Single(version => !version.InLibrary));

        Assert.AreEqual(0, done.Done);
        StringAssert.Contains(done.Skipped.Single(), "kept.mkv");
        Assert.IsTrue(File.Exists(download));
    }

    [TestMethod]
    public async Task Two_library_versions_keep_one_and_never_the_last()
    {
        Write(_movies, @"Night Pier (2021)\Night Pier (2021) - 1080p.mkv", "the bigger film");
        Write(_movies, @"Night Pier 2021\Night.Pier.2021.720p.mkv", "small film");

        var group = (await _service.ScanAsync()).Versions.Single();

        Assert.AreEqual(1, (await _service.RemoveVersionAsync(group, group.Versions[1])).Done);

        var last = await _service.RemoveVersionAsync(group, group.Versions[0]);

        Assert.AreEqual(0, last.Done);
        Assert.IsTrue(File.Exists(group.Versions[0].Path), "The only one left stays.");
    }

    [TestMethod]
    public async Task Extras_and_trailers_are_not_versions()
    {
        Write(_movies, @"Night Pier (2021)\Night Pier (2021).mkv", "the film");
        Write(_movies, @"Night Pier (2021)\Night Pier (2021)-trailer.mkv", "a trailer");
        Write(_movies, @"Night Pier (2021)\Featurettes\Making of.mkv", "making of");
        Write(_movies, @"Night Pier (2021)\Interview with the director.mkv", "an interview");

        Assert.IsEmpty((await _service.ScanAsync()).Versions);
    }

    [TestMethod]
    public async Task Episodes_match_with_or_without_the_show_year()
    {
        Write(_tv, @"Static Dreams (2017)\Season 01\Static Dreams (2017) - S01E02 - Pilot.mkv", "the episode, bigger");
        var download = Write(_torrents, @"TV\Static.Dreams.S01E02.720p\Static.Dreams.S01E02.720p.mkv", "smaller");

        var group = (await _service.ScanAsync()).Versions.Single();

        Assert.AreEqual(MediaType.TvEpisode, group.MediaType);
        Assert.AreEqual("Static Dreams - S01E02", group.DisplayTitle);
        Assert.IsTrue(group.Versions.Any(version => version.DownloadNames.Contains(download)));
    }

    [TestMethod]
    public async Task A_download_the_library_doesnt_have_is_left_for_Hardlinks()
    {
        var download = Write(Torrent("Never.Imported.2020"), "Never.Imported.2020.mkv", "a film");

        var result = await _service.ScanAsync();

        Assert.IsEmpty(result.ToDelete);
        Assert.IsEmpty(result.Versions);
        Assert.DoesNotContain(download, result.MatchedDownloads);
    }

    [TestMethod]
    public async Task Nothing_is_offered_for_deleting_when_the_client_cant_be_reached()
    {
        Write(_movies, @"Night Pier (2021)\Night Pier (2021).mkv", "the film");
        Write(Torrent("Night.Pier.2021.1080p"), "Night.Pier.2021.1080p.mkv", "the film");
        _client.IsReachable = false;

        var result = await _service.ScanAsync();

        Assert.IsFalse(result.ClientReachable);
        Assert.IsEmpty(result.ToDelete);
        Assert.HasCount(1, result.ToLink, "Linking keeps both names, so it is safe either way.");
    }

    [TestMethod]
    public async Task A_folder_with_only_leftovers_is_deleted_whole()
    {
        var folder = Torrent("Old Download");
        Write(folder, "film.srt", "subs");
        Write(folder, "film.nfo", "info");

        var leftover = (await _service.ScanAsync()).ToDelete.Single();

        Assert.AreEqual(DownloadLeftoverKind.LeftoversOnly, leftover.Kind);
        Assert.AreEqual(1, (await _service.DeleteAsync([leftover])).Done);
        Assert.IsFalse(Directory.Exists(folder));
    }

    [TestMethod]
    public async Task A_library_folder_holding_the_downloads_folder_never_pairs_a_file_with_itself()
    {
        // The movie library set to the whole drive, downloads inside it.
        var drive = Path.Combine(Folder, "d");
        var downloads = Path.Combine(drive, "downloads");
        var film = Write(drive, @"Night Pier (2021)\Night Pier (2021).mkv", "same-bytes");
        Write(downloads, @"Night.Pier.2021.1080p\Night.Pier.2021.1080p.mkv", "same-bytes");
        Write(downloads, @"Other.Film.2022.1080p\Other.Film.2022.1080p.mkv", "not in the library");

        var settings =
            new DriveSettings(
                new SimplifiarrSettings
                {
                    DriveProfiles = [new DriveProfile { Name = "D", DownloadPath = downloads, MovieLibraryPath = drive }]
                });

        var history = new HistoryService(settings);
        var inventory = new FileInventoryCacheService(new FileScannerService());

        var service =
            new CopiesService(
                settings,
                inventory,
                _client,
                new ReleaseParser(),
                new DownloadCleanupService(settings, inventory, _client, new HardlinkService(), history, new ReleaseParser(), _quarantine),
                history)
            {
                VersionMinimumBytes = 1,
                ListingMaxAge = TimeSpan.Zero
            };

        var result = await service.ScanAsync();

        // The real copy is found, and the download the library doesn't have isn't a copy of itself.
        var leftover = result.ToDelete.Single();
        Assert.AreEqual(film, leftover.Media.Single().LibraryPath);
        Assert.IsEmpty(result.Versions);
    }

    [TestMethod]
    public async Task Daily_episodes_are_told_apart_by_their_date()
    {
        Write(_tv, @"Late Desk\Season 31\Late.Desk.2026.11.16.Guest.One.720p.WEB.mkv", "one episode");
        Write(_tv, @"Late Desk\Season 31\Late.Desk.2026.11.18.Guest.Two.720p.WEB.mkv", "another episode");
        Write(_tv, @"Late Desk\Season 31\Late.Desk.2026.11.18.Guest.Two.1080p.WEB.mkv", "the same episode, bigger");

        var group = (await _service.ScanAsync()).Versions.Single();

        Assert.AreEqual("tv|latedesk|d|2026-11-18", group.Identity, "Only the two of the 18th are versions of each other.");
        Assert.AreEqual(MediaType.TvEpisode, group.MediaType);
        StringAssert.EndsWith(group.DisplayTitle, "2026-11-18");
    }

    [TestMethod]
    public void A_tv_library_never_holds_movies()
    {
        var parser = new ReleaseParser();

        Assert.IsNull(CopiesService.VideoIdentity(parser, @"X:\TV\Night Pier (2021)\Night Pier (2021).mkv", true, CopiesService.LibraryKind.Tv));
        Assert.AreEqual("movie|nightpier|2021", CopiesService.VideoIdentity(parser, @"X:\Movies\Night Pier (2021)\Night Pier (2021).mkv", true, CopiesService.LibraryKind.Movies));
    }

    [TestMethod]
    public void A_name_that_says_nothing_takes_its_folder_only_when_alone()
    {
        var parser = new ReleaseParser();
        var path = @"X:\Torrents\Night.Pier.2021.1080p\abc123.mkv";

        Assert.AreEqual("movie|nightpier|2021", CopiesService.VideoIdentity(parser, path, onlyBigVideoInFolder: true));
        Assert.IsNull(CopiesService.VideoIdentity(parser, path, onlyBigVideoInFolder: false));
        Assert.AreEqual("movie|nightpier|2021|p2", CopiesService.VideoIdentity(parser, @"X:\Movies\Night Pier (2021)\Night Pier (2021) CD2.mkv", false));
    }
}
