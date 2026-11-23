using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

internal sealed class FakeQuarantine : IQuarantineService
{
    public List<string> Files { get; } = [];

    public Task<string?> QuarantineFileAsync(string path)
    {
        Files.Add(path);
        File.Delete(path);

        return Task.FromResult<string?>(path + ".quarantined");
    }
}

[TestClass]
public class SeasonCleanupTests : TempFolderTest
{
    private string _show = string.Empty;
    private FakeArrPaths _arr = null!;
    private FakeTorrentClient _torrents = null!;
    private FakeQuarantine _quarantine = null!;
    private SeasonCleanupService _service = null!;

    private static readonly ArrFolderFormats SonarrFormats = new() { SeasonFolderFormat = "Season {season}" };

    [TestInitialize]
    public void SetUp()
    {
        _show = Path.Combine(Folder, "TV", "Night Shift");

        Write(@"Season 06\Night.Shift.S06E01.mkv");
        Write(@"Season 06\Night.Shift.S06E02.mkv");
        Write(@"Season 06\Night.Shift.S06E02.nfo");
        Write(@"Season 06\folder.jpg");
        Write(@"Season 6\Night Shift S06E03.mkv");
        Write(@"Season 6\folder.jpg");
        Write(@"Season 1\Night Shift S01E01.mkv");

        // Season 6 is Sonarr's, and was made after Season 06.
        Directory.SetCreationTimeUtc(Path.Combine(_show, "Season 06"), new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc));
        Directory.SetCreationTimeUtc(Path.Combine(_show, "Season 6"), new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc));

        var settings = new DriveSettings(new SimplifiarrSettings
        {
            DriveProfiles = [new DriveProfile { Name = "D", TvLibraryPath = Path.Combine(Folder, "TV") }]
        });

        _arr = new FakeArrPaths();
        _arr.All.Add(new ArrItem("Sonarr", 3, "Night Shift", _show));
        _torrents = new FakeTorrentClient();
        _quarantine = new FakeQuarantine();

        _service =
            new SeasonCleanupService(
                settings,
                _arr,
                new FakeSonarr { Formats = SonarrFormats },
                _torrents,
                new JellyfinClient(new HttpClient(), settings),
                _quarantine,
                new HistoryService(settings));
    }

    private void Write(string relative)
    {
        var path = Path.Combine(_show, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relative);
    }

    [TestMethod]
    public async Task A_split_season_is_found_and_kept_in_Sonarrs_folder_with_the_reason()
    {
        var splits = await _service.FindAsync();

        Assert.HasCount(1, splits, "Season 1 is in one folder, so only season 6 is split.");

        var split = splits[0];

        Assert.AreEqual(6, split.Season);
        Assert.AreEqual("Season 6", split.KeepName);
        Assert.AreEqual("Season 06", split.Strays.Single().Name);
        Assert.AreEqual(2, split.Strays.Single().Episodes);
        StringAssert.Contains(split.Why, "Season {season}");
        StringAssert.Contains(split.Why, "made earlier");
    }

    [TestMethod]
    public void Without_Sonarr_the_folder_with_the_most_episodes_is_kept()
    {
        var split = SeasonCleanupService.Find(_show, null, null).Single();

        Assert.AreEqual("Season 06", split.KeepName);
    }

    [TestMethod]
    public async Task Merging_moves_everything_into_Sonarrs_folder_and_keeps_its_own_picture()
    {
        var split = (await _service.FindAsync()).Single();

        var result = await _service.MergeAsync(split);

        Assert.IsTrue(result.Merged, result.Message);
        Assert.AreEqual(3, result.Moved);
        Assert.AreEqual(1, result.Quarantined, "Season 6 already has a folder.jpg.");
        Assert.IsFalse(Directory.Exists(Path.Combine(_show, "Season 06")), "The emptied folder is removed.");
        Assert.IsTrue(File.Exists(Path.Combine(_show, "Season 6", "Night.Shift.S06E01.mkv")));
        Assert.AreEqual(@"Season 6\folder.jpg", File.ReadAllText(Path.Combine(_show, "Season 6", "folder.jpg")));
        CollectionAssert.Contains(_arr.Rescans, 3);
    }

    [TestMethod]
    public async Task A_file_a_torrent_is_seeding_from_is_left_where_it_is()
    {
        var seeded = Path.Combine(_show, "Season 06", "Night.Shift.S06E01.mkv");

        _torrents.Torrents.Add(new TorrentRecord { TorrentHash = "a", ContentPath = seeded, Status = TorrentStatus.Seeding });

        var result = await _service.MergeAsync((await _service.FindAsync()).Single());

        Assert.IsFalse(result.Merged);
        Assert.IsTrue(File.Exists(seeded));
        Assert.IsTrue(File.Exists(Path.Combine(_show, "Season 6", "Night.Shift.S06E02.mkv")));
        StringAssert.Contains(result.Message, "seeding");
    }
}
