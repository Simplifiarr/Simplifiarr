using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

[TestClass]
public class DownloadsDriveTests : TempFolderTest
{
    private string _movies = default!;
    private string _tv = default!;
    private string _torrents = default!;
    private string _overflow = default!;
    private SimplifiarrSettings _settings = default!;
    private FakeTorrentClient _client = default!;
    private FakeArrPaths _arr = default!;
    private StorageMoveService _mover = default!;

    [TestInitialize]
    public void Build()
    {
        _movies = Path.Combine(Folder, "c", "Movies");
        _tv = Path.Combine(Folder, "c", "TV");
        _torrents = Path.Combine(Folder, "c", "Torrents");
        _overflow = Path.Combine(Folder, "h", "Movies");

        foreach (var folder in new[] { _movies, _tv, _torrents, _overflow })
        {
            Directory.CreateDirectory(folder);
        }

        _settings =
            new SimplifiarrSettings
            {
                DownloadsDrive = "Main",
                DriveProfiles =
                [
                    new DriveProfile { Name = "Main", DownloadPath = _torrents, MovieLibraryPath = _movies, TvLibraryPath = _tv },
                    new DriveProfile { Name = "Overflow", MovieLibraryPath = _overflow }
                ]
            };

        var settings = new DriveSettings(_settings);

        _client = new FakeTorrentClient();
        _arr = new FakeArrPaths();

        _mover =
            new StorageMoveService(
                _arr,
                new StorageService(settings, new FileInventoryCacheService(new FileScannerService())),
                new HistoryService(settings),
                new IdleScans(),
                settings: settings,
                client: _client)
            {
                RequireOtherDrive = false,
                PauseBetweenTitles = TimeSpan.Zero
            };
    }

    private (StorageTitle Title, string Film) Movie(string name)
    {
        var folder = Path.Combine(_movies, name);
        var film = Path.Combine(folder, name + ".mkv");

        Directory.CreateDirectory(folder);
        File.WriteAllText(film, "the film");

        return (new StorageTitle { Name = name, Kind = StorageKinds.Movies, FolderPath = folder, LibraryPath = _movies, SizeBytes = 8 }, film);
    }

    private string LinkInto(string film, string folder, string name)
    {
        Directory.CreateDirectory(folder);

        var link = Path.Combine(folder, name);

        Assert.IsNull(NativeHardlinks.CreateHardlink(film, link));

        return link;
    }

    [TestMethod]
    public async Task A_leftover_download_name_goes_with_the_move_so_the_space_comes_back()
    {
        var (title, film) = Movie("Night Pier (2021)");
        var leftover = LinkInto(film, Path.Combine(_torrents, "Night.Pier.2021.1080p"), "Night.Pier.2021.1080p.mkv");

        var result = await _mover.MoveAsync(new StorageMoveRequest(title, _overflow));

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsFalse(File.Exists(leftover), "No torrent used it, so it went with the move.");
        Assert.IsFalse(Directory.Exists(title.FolderPath));
        Assert.AreEqual("the film", File.ReadAllText(Path.Combine(_overflow, "Night Pier (2021)", "Night Pier (2021).mkv")));
        StringAssert.Contains(result.Message, "leftover download");
    }

    [TestMethod]
    public async Task A_title_a_torrent_still_seeds_stays_until_it_is_done()
    {
        var (title, film) = Movie("Night Pier (2021)");
        var seed = LinkInto(film, Path.Combine(_torrents, "Night.Pier.2021.1080p"), "Night.Pier.2021.1080p.mkv");
        _client.Torrents.Add(new TorrentRecord { Name = "Night.Pier.2021.1080p", ContentPath = Path.GetDirectoryName(seed)! });

        var result = await _mover.MoveAsync(new StorageMoveRequest(title, _overflow));

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "still seeding");
        Assert.IsTrue(File.Exists(film));
        Assert.IsTrue(File.Exists(seed));
    }

    [TestMethod]
    public async Task A_name_left_in_the_Recycle_Bin_goes_too()
    {
        var (title, film) = Movie("Night Pier (2021)");
        var bin = Path.Combine(Folder, "$Recycle.Bin", "S-1-5-18");
        var recycled = LinkInto(film, bin, "$RABC123.mkv");
        File.WriteAllText(Path.Combine(bin, "$IABC123.mkv"), "where it came from");

        var result = await _mover.MoveAsync(new StorageMoveRequest(title, _overflow));

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsFalse(File.Exists(recycled));
        Assert.IsFalse(File.Exists(Path.Combine(bin, "$IABC123.mkv")), "Its description goes with it.");
    }

    [TestMethod]
    public async Task A_name_somewhere_else_keeps_the_title_where_it_is()
    {
        var (title, film) = Movie("Night Pier (2021)");
        LinkInto(film, Path.Combine(Folder, "c", "Collections", "Night Pier"), "Night Pier (2021).mkv");

        var result = await _mover.MoveAsync(new StorageMoveRequest(title, _overflow));

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "Collections");
        Assert.IsTrue(File.Exists(film));
    }

    [TestMethod]
    public async Task Shows_still_airing_stay_on_the_downloads_drive()
    {
        var airing = Path.Combine(_tv, "Static Dreams (2017)");
        var ended = Path.Combine(_tv, "Old Harbour (2009)");

        foreach (var show in new[] { airing, ended })
        {
            Directory.CreateDirectory(Path.Combine(show, "Season 01"));
            File.WriteAllText(Path.Combine(show, "Season 01", "episode.mkv"), "an episode");
        }

        _arr.All.Add(new ArrItem("Sonarr", 1, "Static Dreams", airing, Airing: true));
        _arr.All.Add(new ArrItem("Sonarr", 2, "Old Harbour", ended));

        var storage = new StorageService(new DriveSettings(_settings), new FileInventoryCacheService(new FileScannerService()), _client, _arr);

        await storage.ScanAsync("Main");

        var drive = (await storage.GetDrivesAsync()).Single(candidate => candidate.ProfileNames.Contains("Main"));
        var titles = drive.Titles.Where(title => title.Kind == StorageKinds.Tv).ToDictionary(title => title.FolderPath, StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(drive.IsDownloadsDrive);
        Assert.IsTrue(drive.IsProtected, "The downloads drive is the one kept clear.");
        Assert.IsFalse(titles[airing].CanMove);
        Assert.IsTrue(titles[ended].CanMove);
    }

    [TestMethod]
    public void Each_title_is_tagged_to_download_to_the_drive_it_lives_on()
    {
        // Tag 1 -> O, tag 2 -> F (movies), tag 3 -> F (a seasonal collection), tag 4 -> H.
        var routing = new Dictionary<int, string> { [1] = @"O:\", [2] = @"F:\", [3] = @"F:\", [4] = @"H:\" };

        var titles =
            new List<TaggedTitle>
            {
                new(1, @"C:\Library\Movies\Night Pier (2021)", @"C:\Library\Movies", [4]),
                new(2, @"O:\Movies\Harbour (2019)", @"O:\Movies", []),
                new(3, @"O:\Movies\Twin (2020)", @"O:\Movies", [1]),
                new(4, @"F:\Seasonal\Snow Day (2015)", @"F:\Seasonal", [1]),
                new(5, @"F:\Seasonal\Gift Night (2018)", @"F:\Seasonal", [3]),
                new(6, @"F:\Movies\Long Road (2010)", @"F:\Movies", []),
                new(7, @"Z:\Movies\Elsewhere (2011)", @"Z:\Movies", []),
                new(8, @"H:\Movies\Hill (2012)", @"H:\Movies", [4, 99])
            };

        var plan = StackSetupService.PlanRetag(titles, routing, @"C:\");

        Assert.AreEqual(4, plan.Titles);
        CollectionAssert.AreEquivalent(new[] { 1 }, plan.Remove[4], "On the downloads drive: no drive tag.");
        CollectionAssert.AreEquivalent(new[] { 4 }, plan.Remove[1], "On F, so not O's tag.");
        CollectionAssert.AreEquivalent(new[] { 2 }, plan.Add[1]);
        CollectionAssert.AreEquivalent(new[] { 4 }, plan.Add[3], "The tag the rest of its folder uses for F.");
        CollectionAssert.AreEquivalent(new[] { 6 }, plan.Add[2]);
        Assert.AreEqual(1, plan.NoClientForDrive, "No client saves to Z: left alone.");
    }

    [TestMethod]
    public void A_moved_title_alone_is_retagged()
    {
        var routing = new Dictionary<int, string> { [1] = @"O:\", [4] = @"H:\" };

        var titles =
            new List<TaggedTitle>
            {
                new(1, @"H:\Movies\Night Pier (2021)", @"H:\Movies", []),
                new(2, @"O:\Movies\Harbour (2019)", @"O:\Movies", [])
            };

        var plan = StackSetupService.PlanRetag(titles, routing, @"C:\", onlyId: 1);

        Assert.AreEqual(1, plan.Titles);
        CollectionAssert.AreEquivalent(new[] { 1 }, plan.Add[4]);
        Assert.IsFalse(plan.Add.ContainsKey(1), "Only the moved title changes.");
    }

    [TestMethod]
    public void A_category_with_no_folder_saves_under_the_default_one()
    {
        var layout =
            new TorrentClientLayout(
                @"C:\Torrents",
                new Dictionary<string, string> { ["tv"] = string.Empty, ["movies-o"] = "O:/Torrents/Movies" });

        Assert.AreEqual(@"C:\Torrents\tv", layout.FolderOf("tv"));
        Assert.AreEqual(@"O:\Torrents\Movies", layout.FolderOf("movies-o"));
        Assert.AreEqual(@"C:\Torrents", layout.FolderOf("unknown"));
    }
}
