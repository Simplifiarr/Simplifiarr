using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class RollingMoviePlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<string, long> NotFull = new Dictionary<string, long>();

    private const long GB = 1024L * 1024 * 1024;

    private static RollingMovieFacts Movie(
        int id,
        string name,
        int arrivedDaysAgo = 100,
        string folder = @"D:\Movies",
        string[]? tags = null,
        string? requestedBy = null,
        Dictionary<string, DateTime?>? watchedOn = null,
        DateTime? requesterWatched = null,
        bool started = false,
        bool favorite = false,
        bool kept = false,
        bool seeding = false,
        long size = 10 * GB) =>
        new(
            new RollingMovie(id, name, 2020, $@"{folder}\{name} (2020)", size, Now.AddDays(-arrivedDaysAgo), tags ?? []),
            requestedBy,
            watchedOn ?? [],
            requesterWatched,
            started || watchedOn?.Count > 0,
            favorite,
            kept,
            seeding,
            Path.GetPathRoot(folder)!,
            0);

    private static List<string> Deleted(RollingMovieRule rule, IReadOnlyList<RollingMovieFacts> movies, IReadOnlyDictionary<string, long>? full = null) =>
        RollingMoviePlanner.Plan(rule, movies, "keep", full ?? NotFull, Now).Deletions
            .Select(deletion => deletion.Facts.Movie.Title)
            .OrderBy(name => name)
            .ToList();

    [TestMethod]
    public void A_movie_matches_by_tag_requester_folder_or_being_picked()
    {
        var movies = new[]
        {
            Movie(1, "Tagged", tags: ["kids"]),
            Movie(2, "Asked", requestedBy: "Sam"),
            Movie(3, "Holiday", folder: @"E:\Holiday"),
            Movie(4, "Picked"),
            Movie(5, "Other", requestedBy: "Alex")
        };

        var rule = new RollingMovieRule
        {
            Tags = ["KIDS"],
            RequestedBy = ["Sam"],
            Folders = [@"E:\Holiday"],
            MovieIds = [4],
            DaysAfterAdded = 30
        };

        CollectionAssert.AreEqual(new[] { "Asked", "Holiday", "Picked", "Tagged" }, Deleted(rule, movies));

        rule.AnyRequest = true;
        CollectionAssert.Contains(Deleted(rule, movies), "Other");
    }

    [TestMethod]
    public void Days_after_watched_follows_the_requester_and_falls_back_to_anyone()
    {
        var movies = new[]
        {
            // Sam asked and watched 20 days ago.
            Movie(1, "Watched by asker", requestedBy: "Sam", watchedOn: new() { ["Sam"] = Now.AddDays(-20) }, requesterWatched: Now.AddDays(-20)),
            // Sam asked; only Alex has watched it.
            Movie(2, "Watched by someone else", requestedBy: "Sam", watchedOn: new() { ["Alex"] = Now.AddDays(-20) }),
            // Nobody asked; Alex watched it.
            Movie(3, "Not requested", watchedOn: new() { ["Alex"] = Now.AddDays(-20) }),
            // Watched only yesterday.
            Movie(4, "Too recent", requestedBy: "Sam", watchedOn: new() { ["Sam"] = Now.AddDays(-1) }, requesterWatched: Now.AddDays(-1))
        };

        var rule = new RollingMovieRule { Folders = [@"D:\Movies"], DaysAfterWatched = 14, WatchedBy = MovieWatcher.Requester };

        CollectionAssert.AreEqual(new[] { "Not requested", "Watched by asker" }, Deleted(rule, movies));

        rule.WatchedBy = MovieWatcher.Anyone;
        CollectionAssert.AreEqual(new[] { "Not requested", "Watched by asker", "Watched by someone else" }, Deleted(rule, movies));
    }

    [TestMethod]
    public void Picked_people_must_all_have_watched()
    {
        var both = Movie(1, "Both", watchedOn: new() { ["Alex"] = Now.AddDays(-30), ["Sam"] = Now.AddDays(-20) });
        var one = Movie(2, "One", watchedOn: new() { ["Alex"] = Now.AddDays(-30) });

        var rule = new RollingMovieRule
        {
            Folders = [@"D:\Movies"],
            DaysAfterWatched = 14,
            WatchedBy = MovieWatcher.People,
            WatchedByUsers = ["alex", "Sam"]
        };

        CollectionAssert.AreEqual(new[] { "Both" }, Deleted(rule, [both, one]));

        // The clock starts when the last of them watched.
        rule.DaysAfterWatched = 25;
        Assert.IsEmpty(Deleted(rule, [both, one]));
    }

    [TestMethod]
    public void Unwatched_for_months_and_days_after_arriving()
    {
        var movies = new[]
        {
            Movie(1, "Old unwatched", arrivedDaysAgo: 200),
            Movie(2, "Old but started", arrivedDaysAgo: 200, started: true),
            Movie(3, "New", arrivedDaysAgo: 10)
        };

        var rule = new RollingMovieRule { Folders = [@"D:\Movies"], UnwatchedMonths = 6 };
        CollectionAssert.AreEqual(new[] { "Old unwatched" }, Deleted(rule, movies));

        rule = new RollingMovieRule { Folders = [@"D:\Movies"], DaysAfterAdded = 30 };
        CollectionAssert.AreEqual(new[] { "Old but started", "Old unwatched" }, Deleted(rule, movies));
    }

    [TestMethod]
    public void Favorites_kept_and_the_keep_tag_always_stay()
    {
        var movies = new[]
        {
            Movie(1, "Favorite", favorite: true),
            Movie(2, "Kept", kept: true),
            Movie(3, "Tagged keep", tags: ["Keep"]),
            Movie(4, "Plain")
        };

        var plan = RollingMoviePlanner.Plan(new RollingMovieRule { Folders = [@"D:\Movies"], DaysAfterAdded = 1 }, movies, "keep", NotFull, Now);

        CollectionAssert.AreEqual(new[] { "Plain" }, plan.Deletions.Select(deletion => deletion.Facts.Movie.Title).ToList());
        Assert.AreEqual(4, plan.Matched);
        Assert.AreEqual(3, plan.Protected);
    }

    [TestMethod]
    public void A_full_drive_loses_the_longest_since_watched_first_until_it_is_under()
    {
        var movies = new[]
        {
            Movie(1, "Watched long ago", watchedOn: new() { ["Alex"] = Now.AddDays(-300) }),
            Movie(2, "Watched lately", watchedOn: new() { ["Alex"] = Now.AddDays(-10) }),
            Movie(3, "Never watched", arrivedDaysAgo: 400),
            Movie(4, "Seeding", watchedOn: new() { ["Alex"] = Now.AddDays(-500) }, seeding: true),
            Movie(5, "Just arrived", arrivedDaysAgo: 2),
            Movie(6, "Other drive", folder: @"E:\Movies", watchedOn: new() { ["Alex"] = Now.AddDays(-900) })
        };

        var rule = new RollingMovieRule { Folders = [@"D:\Movies", @"E:\Movies"], WhenDriveFull = true, WatchedBy = MovieWatcher.Anyone };

        // D: is 15 GB over: two 10 GB movies get it under.
        var full = new Dictionary<string, long> { [@"D:\"] = 15 * GB };

        CollectionAssert.AreEqual(new[] { "Watched lately", "Watched long ago" }, Deleted(rule, movies, full));

        // 35 GB over: the watched ones, then the never-watched one; never the seeding or new one.
        full[@"D:\"] = 35 * GB;
        CollectionAssert.AreEqual(new[] { "Never watched", "Watched lately", "Watched long ago" }, Deleted(rule, movies, full));

        Assert.IsEmpty(Deleted(rule, movies), "Nothing goes while no drive is over its limit.");
    }

    [TestMethod]
    public void Nothing_without_movies_or_a_trigger_and_the_sentences_read_well()
    {
        var movies = new[] { Movie(1, "Plain") };

        Assert.IsEmpty(Deleted(new RollingMovieRule { DaysAfterAdded = 1 }, movies));
        Assert.IsEmpty(Deleted(new RollingMovieRule { Folders = [@"D:\Movies"] }, movies));

        var rule = new RollingMovieRule
        {
            Tags = ["kids"],
            RequestedBy = ["Sam", "Alex"],
            DaysAfterWatched = 14,
            UnwatchedMonths = 6
        };

        Assert.AreEqual("Movies tagged kids, or requested by Sam or Alex", RollingMoviePlanner.DescribeWhich(rule));
        Assert.AreEqual("Deleted 14 days after whoever asked for it watches, or after 6 months unwatched", RollingMoviePlanner.DescribeWhen(rule));
    }
}

[TestClass]
public class RadarrArrivalTests
{
    private static DateTime? Arrived(string movieJson, string? fileJson)
    {
        using var movie = System.Text.Json.JsonDocument.Parse(movieJson);
        using var file = System.Text.Json.JsonDocument.Parse(fileJson ?? "null");

        return RadarrMovieClient.Arrived(movie.RootElement, file.RootElement);
    }

    [TestMethod]
    public void A_reimported_file_does_not_restart_the_clock()
    {
        // Added in March, moved (re-imported) yesterday.
        Assert.AreEqual(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Arrived("""{"added":"2026-03-01T00:00:00Z","digitalRelease":"2020-01-01T00:00:00Z"}""", """{"dateAdded":"2026-11-21T00:00:00Z"}"""));
    }

    [TestMethod]
    public void Asked_for_before_release_counts_from_the_release_but_never_after_the_file()
    {
        // Requested in January, out digitally in May, downloaded in May.
        Assert.AreEqual(
            new DateTime(2026, 5, 10, 0, 0, 0, DateTimeKind.Utc),
            Arrived("""{"added":"2026-01-01T00:00:00Z","digitalRelease":"2026-05-10T00:00:00Z"}""", """{"dateAdded":"2026-06-01T00:00:00Z"}"""));

        // Grabbed early, before the digital release date.
        Assert.AreEqual(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            Arrived("""{"added":"2026-01-01T00:00:00Z","digitalRelease":"2026-05-10T00:00:00Z"}""", """{"dateAdded":"2026-04-01T00:00:00Z"}"""));
    }
}

[TestClass]
public class WatchHistoryFavoriteTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Each_watch_date_favorites_and_the_requesters_watch_are_kept()
    {
        const string path = @"D:\Movies\Harbour (2019)\Harbour.mkv";

        var records =
            WatchHistoryService.Combine(
                [
                    ("u-1", "Alex", [new JellyfinWatchItem("m1", false, path, Now.AddMonths(-3), true, true, Now.AddDays(-5), Favorite: true)]),
                    ("u-2", "Sam", [new JellyfinWatchItem("m1", false, path, Now.AddMonths(-3), true, true, Now.AddDays(-2))])
                ],
                [new SeerrRequest(7, "m1", "Sam", "u2", Now.AddMonths(-4))]);

        var record = records.Single();

        Assert.AreEqual(Now.AddDays(-5), record.WatchedOn["Alex"]);
        Assert.AreEqual(Now.AddDays(-2), record.WatchedOn["Sam"]);
        CollectionAssert.AreEqual(new[] { "Alex" }, record.FavoriteOf);
        Assert.AreEqual(Now.AddDays(-2), record.RequesterWatchedUtc);
    }
}
