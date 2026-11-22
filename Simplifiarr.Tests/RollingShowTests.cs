using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

[TestClass]
public class RollingShowPlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    private static ShowEpisode Episode(int season, int number, int daysAgo) =>
        new(season * 100 + number, season * 100 + number, season, number, $"Episode {number}", Now.AddDays(-daysAgo), $@"D:\TV\Night Shift\Season {season}\E{number}.mkv", 1000, true);

    // A weekly show: one episode a week, newest first.
    private static List<ShowEpisode> Weekly(int count, int season = 1) =>
        Enumerable.Range(1, count).Select(number => Episode(season, number, (count - number) * 7)).ToList();

    private static List<string> Deleted(RollingShowRule rule, IReadOnlyList<ShowEpisode> episodes, Func<ShowEpisode, bool>? watched = null) =>
        RollingShowPlanner.Plan(rule, episodes, watched ?? (_ => false), Now)
            .Select(deletion => $"S{deletion.Episode.Season}E{deletion.Episode.Number}")
            .OrderBy(name => name)
            .ToList();

    [TestMethod]
    public void Keep_the_newest_four_of_a_weekly_show()
    {
        CollectionAssert.AreEqual(new[] { "S1E1", "S1E2" }, Deleted(new RollingShowRule { KeepLatest = 4 }, Weekly(6)));
    }

    [TestMethod]
    public void Keep_seven_days_but_never_fewer_than_three()
    {
        // Daily, but the show was off for a month: the newest aired 30 days ago.
        var daily = Enumerable.Range(1, 5).Select(number => Episode(1, number, 30 + (5 - number))).ToList();

        Assert.HasCount(5, Deleted(new RollingShowRule { KeepDays = 7 }, daily));
        CollectionAssert.AreEqual(new[] { "S1E1", "S1E2" }, Deleted(new RollingShowRule { KeepDays = 7, MinimumKeep = 3 }, daily));
    }

    [TestMethod]
    public void The_old_season_goes_when_the_new_one_starts()
    {
        var episodes = Weekly(3, season: 35).Select(episode => episode with { AiredUtc = episode.AiredUtc!.Value.AddDays(-60) }).ToList();

        var rule = new RollingShowRule { KeepCurrentSeasonOnly = true };

        Assert.IsEmpty(Deleted(rule, episodes), "No new season yet.");

        episodes.Add(Episode(36, 1, 0));

        CollectionAssert.AreEqual(new[] { "S35E1", "S35E2", "S35E3" }, Deleted(rule, episodes));
    }

    [TestMethod]
    public void The_old_season_can_wait_for_a_few_new_episodes()
    {
        var episodes = Weekly(3, season: 35);
        episodes.Add(Episode(36, 1, 0));

        var rule = new RollingShowRule { KeepCurrentSeasonOnly = true, SeasonSwitch = SeasonSwitch.AfterEpisodes, SeasonSwitchValue = 2 };

        Assert.IsEmpty(Deleted(rule, episodes));

        episodes.Add(Episode(36, 2, 0));

        Assert.HasCount(3, Deleted(rule, episodes));
    }

    [TestMethod]
    public void Keeping_two_seasons_keeps_the_new_one_and_the_one_before()
    {
        var episodes = Weekly(2, season: 34).Concat(Weekly(2, season: 35)).ToList();
        episodes.Add(Episode(36, 1, 0));

        var rule = new RollingShowRule();
        rule.SetSimple(2, KeepUnit.Seasons);

        CollectionAssert.AreEqual(new[] { "S34E1", "S34E2" }, Deleted(rule, episodes));
    }

    [TestMethod]
    public void The_sentence_sets_one_limit_and_reads_back_the_same()
    {
        var rule = new RollingShowRule { KeepLatest = 4 };

        rule.SetSimple(2, KeepUnit.Weeks);

        Assert.AreEqual(14, rule.KeepDays);
        Assert.IsNull(rule.KeepLatest, "Only one limit at a time.");
        Assert.AreEqual((2, KeepUnit.Weeks), rule.Simple);
        Assert.AreEqual("Keeps the last 2 weeks", RollingShowPlanner.Describe(rule));

        // A rule saved before seasons could be counted reads as 1 season.
        Assert.AreEqual((1, KeepUnit.Seasons), new RollingShowRule { KeepCurrentSeasonOnly = true }.Simple);

        rule.SetSimple(5, KeepUnit.Episodes);
        rule.Watched = WatchRequirement.Anyone;

        Assert.AreEqual("Keeps the last 5 episodes, once watched", RollingShowPlanner.Describe(rule));
    }

    [TestMethod]
    public void Unwatched_episodes_wait_unless_they_have_waited_long_enough()
    {
        var episodes = Weekly(6);
        var rule = new RollingShowRule { KeepLatest = 4, Watched = WatchRequirement.Anyone };

        // Only episode 2 has been watched.
        CollectionAssert.AreEqual(new[] { "S1E2" }, Deleted(rule, episodes, episode => episode.Number == 2));

        rule.UnwatchedAfterDays = 30;

        // Episode 1 aired 35 days ago, so it goes anyway.
        CollectionAssert.AreEqual(new[] { "S1E1", "S1E2" }, Deleted(rule, episodes, episode => episode.Number == 2));
    }

    [TestMethod]
    public void Picking_two_people_waits_for_both_of_them()
    {
        var perUser = new List<(string Name, IEnumerable<string> Paths)>
        {
            ("Alex", new[] { "E1", "E2" }),
            ("Sam", new[] { "e2", "E3" }),
            ("Robin", new[] { "E4" })
        };

        var rule = new RollingShowRule { Watched = WatchRequirement.User, WatchedByUsers = ["Alex", "Sam"] };

        CollectionAssert.AreEquivalent(new[] { "E2" }, RollingShowPlanner.WatchedPaths(rule, perUser).ToList());
        Assert.AreEqual("Keeps the last 5 episodes, once Alex and Sam have watched", Describe(rule));

        rule.Watched = WatchRequirement.Anyone;
        Assert.HasCount(4, RollingShowPlanner.WatchedPaths(rule, perUser));

        // A rule saved with one name still works, and someone gone from Jellyfin holds everything back.
        var old = new RollingShowRule { Watched = WatchRequirement.User, WatchedBy = "Robin" };
        CollectionAssert.AreEquivalent(new[] { "E4" }, RollingShowPlanner.WatchedPaths(old, perUser).ToList());
        Assert.AreEqual("Keeps the last 5 episodes, once Robin has watched", Describe(old));

        old.WatchedByUsers = ["Robin", "Gone"];
        Assert.IsEmpty(RollingShowPlanner.WatchedPaths(old, perUser));

        static string Describe(RollingShowRule rule)
        {
            rule.SetSimple(5, KeepUnit.Episodes);
            return RollingShowPlanner.Describe(rule);
        }
    }

    [TestMethod]
    public void Specials_and_a_rule_without_limits_delete_nothing()
    {
        var episodes = Weekly(6);
        episodes.Add(Episode(0, 1, 400));

        Assert.IsEmpty(Deleted(new RollingShowRule(), episodes));
        Assert.DoesNotContain("S0E1", Deleted(new RollingShowRule { KeepLatest = 1 }, episodes));
    }

    [TestMethod]
    public void A_seed_is_done_by_days_ratio_or_the_client_stopping_it()
    {
        var seed = new RollingSeed { TaggedUtc = Now.AddDays(-1), SeedDays = 7, SeedRatio = 2 };

        Assert.IsFalse(RollingShowService.HasSeededEnough(seed, new TorrentRecord { Progress = 1, Ratio = 0.5, SeedingTime = TimeSpan.FromDays(1), Status = TorrentStatus.Seeding }, Now));
        Assert.IsTrue(RollingShowService.HasSeededEnough(seed, new TorrentRecord { Progress = 1, Ratio = 2.1, Status = TorrentStatus.Seeding }, Now));
        Assert.IsTrue(RollingShowService.HasSeededEnough(seed, new TorrentRecord { Progress = 1, SeedingTime = TimeSpan.FromDays(8), Status = TorrentStatus.Seeding }, Now));
        Assert.IsTrue(RollingShowService.HasSeededEnough(seed, new TorrentRecord { Progress = 1, Status = TorrentStatus.Stopped }, Now));
        Assert.IsFalse(RollingShowService.HasSeededEnough(seed, new TorrentRecord { Progress = 0.5, Status = TorrentStatus.Stopped }, Now));
    }
}

internal sealed class FakeSonarrShows : ISonarrShowClient
{
    public List<ShowEpisode> Episodes { get; } = [];

    public List<int> Unmonitored { get; } = [];

    public int Rescans { get; private set; }

    public Task<IReadOnlyList<SonarrShow>> GetShowsAsync() =>
        Task.FromResult<IReadOnlyList<SonarrShow>>([new SonarrShow(1, "Night Shift", @"D:\TV\Night Shift")]);

    public Task<IReadOnlyList<ShowEpisode>> GetEpisodesWithFilesAsync(int seriesId) =>
        Task.FromResult<IReadOnlyList<ShowEpisode>>(Episodes.Where(episode => File.Exists(episode.FilePath)).ToList());

    public Task<string?> UnmonitorAsync(IReadOnlyCollection<int> episodeIds)
    {
        Unmonitored.AddRange(episodeIds);

        return Task.FromResult<string?>(null);
    }

    public Task RescanAsync(int seriesId)
    {
        Rescans++;

        return Task.CompletedTask;
    }
}

[TestClass]
public class RollingShowServiceTests : TempFolderTest
{
    [TestMethod]
    public async Task Turning_a_rule_on_unmonitors_deletes_and_tags_a_seeding_torrent()
    {
        var show = Path.Combine(Folder, "TV", "Night Shift", "Season 1");
        var downloads = Path.Combine(Folder, "Torrents", "TV");

        Directory.CreateDirectory(show);
        Directory.CreateDirectory(downloads);

        var sonarr = new FakeSonarrShows();
        var now = DateTime.UtcNow;

        for (var number = 1; number <= 3; number++)
        {
            var file = Path.Combine(show, $"E{number}.mkv");
            File.WriteAllText(file, $"episode {number}");

            sonarr.Episodes.Add(new ShowEpisode(number, number, 1, number, $"Episode {number}", now.AddDays(-7 * (3 - number)), file, 9, true));
        }

        // Episode 1 is still seeding from the downloads folder.
        var seedName = Path.Combine(downloads, "Night.Shift.S01E01.mkv");
        Assert.IsNull(NativeHardlinks.CreateHardlink(Path.Combine(show, "E1.mkv"), seedName));

        var client = new FakeTorrentClient();
        client.Torrents.Add(new TorrentRecord { TorrentHash = "aaa", Name = "Night.Shift.S01E01.mkv", ContentPath = seedName, SavePath = downloads, Progress = 1, Status = TorrentStatus.Seeding });

        var settings = new DriveSettings(new SimplifiarrSettings { DriveProfiles = [new DriveProfile { Name = "D", DownloadPath = Path.Combine(Folder, "Torrents"), TvLibraryPath = Path.Combine(Folder, "TV") }] });

        var deleted = new List<string>();

        var service =
            new RollingShowService(sonarr, null!, settings, client, new HistoryService(settings))
            {
                DeleteFile = (path, permanent) =>
                {
                    deleted.Add(path);
                    File.Delete(path);
                    return true;
                }
            };

        var rule = new RollingShowRule { SeriesId = 1, SeriesTitle = "Night Shift", SeriesPath = Path.Combine(Folder, "TV", "Night Shift"), KeepLatest = 2, SeedDays = 7 };

        var preview = await service.PreviewAsync(rule);

        Assert.HasCount(1, preview.Deletions);
        Assert.IsEmpty(deleted, "A preview changes nothing.");

        rule.IsOn = true;
        service.Save(rule);

        var message = await service.RunRuleAsync(rule.Id);

        CollectionAssert.AreEqual(new[] { 1 }, sonarr.Unmonitored, "Sonarr stops looking for it first.");
        CollectionAssert.AreEqual(new[] { Path.Combine(show, "E1.mkv") }, deleted);
        Assert.AreEqual(1, sonarr.Rescans);
        StringAssert.Contains(client.Torrents.Single().Tags, RollingShowService.DeleteTag);
        Assert.HasCount(1, service.Seeds);
        StringAssert.Contains(message, "still seeding");

        // Not seeded enough yet: the torrent stays.
        await service.CleanUpSeedsAsync();
        Assert.IsEmpty(client.Removed);

        // qBittorrent stops it (its share limit): now it goes, with its files.
        client.Torrents.Single().Status = TorrentStatus.Stopped;
        client.Files["aaa"] = [(new TorrentContentFile("Night.Shift.S01E01.mkv", 9), [])];

        await service.CleanUpSeedsAsync();

        Assert.AreEqual(("aaa", true), client.Removed.Single());
        Assert.IsEmpty(service.Seeds);
    }

    [TestMethod]
    public async Task A_rule_that_is_off_never_deletes_on_a_scan()
    {
        var sonarr = new FakeSonarrShows();
        var file = Path.Combine(Folder, "E1.mkv");
        File.WriteAllText(file, "x");
        sonarr.Episodes.Add(new ShowEpisode(1, 1, 1, 1, "One", DateTime.UtcNow.AddDays(-100), file, 1, true));

        var settings = new DriveSettings(new SimplifiarrSettings());
        var service = new RollingShowService(sonarr, null!, settings, new FakeTorrentClient(), new HistoryService(settings));

        service.Save(new RollingShowRule { SeriesId = 1, SeriesTitle = "Night Shift", KeepDays = 7, IsOn = false });

        await service.RunAsync();

        Assert.IsTrue(File.Exists(file));
        Assert.IsEmpty(sonarr.Unmonitored);
    }
}
