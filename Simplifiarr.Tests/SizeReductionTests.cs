using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

/// <summary>
/// A stand-in for Prowlarr that answers searches from memory.
/// </summary>
internal sealed class FakeProwlarr : IProwlarrClient
{
    public Dictionary<string, List<ProwlarrSearchResult>> Releases { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsReachable { get; set; } = true;

    /// <summary>Runs just before each search is answered.</summary>
    public Action? BeforeSearch { get; set; }

    public int Searches { get; private set; }

    public void Add(string query, string title, long sizeBytes)
    {
        if (!Releases.TryGetValue(query, out var list))
        {
            Releases[query] = list = [];
        }

        list.Add(
            new ProwlarrSearchResult
            {
                Title = title,
                SizeBytes = sizeBytes,
                Indexer = "Test indexer",
                DownloadUrl = "http://indexer.test/" + Uri.EscapeDataString(title)
            });
    }

    public Task<List<ProwlarrSearchResult>> SearchAsync(string query, int? categoryId = null)
    {
        Searches++;

        BeforeSearch?.Invoke();

        if (!IsReachable)
        {
            return Task.FromResult(new List<ProwlarrSearchResult>());
        }

        return Task.FromResult(
            Releases.TryGetValue(query, out var list) ? list.ToList() : EveryQuery.ToList());
    }

    /// <summary>Download address -> the .torrent it serves.</summary>
    public Dictionary<string, byte[]> Torrents { get; } = [];

    /// <summary>Answers every search, whatever was asked for.</summary>
    public List<ProwlarrSearchResult> EveryQuery { get; } = [];

    public Task<byte[]?> DownloadTorrentAsync(string downloadUrl) =>
        Torrents.TryGetValue(downloadUrl, out var bytes)
            ? Task.FromResult<byte[]?>(bytes)
            : throw new InvalidOperationException("Nothing asked for this release to be downloaded.");

    public Task<bool> TestConnectionAsync() =>
        Task.FromResult(IsReachable);
}

internal sealed class OneDriveSettings(DriveProfile profile) : ISettingsService
{
    public Task<SimplifiarrSettings> GetSettingsAsync() =>
        Task.FromResult(new SimplifiarrSettings());

    public Task<List<DriveProfile>> GetEnabledDriveProfilesAsync() =>
        Task.FromResult(new List<DriveProfile> { profile });

    public Task SaveSettingsAsync(SimplifiarrSettings settings) =>
        Task.CompletedTask;
}

[TestClass]
public class SizeReductionTests : TempFolderTest
{
    private const long Gb = 1024L * 1024 * 1024;

    private FakeProwlarr _prowlarr = default!;
    private MediaCatalogService _catalog = default!;
    private SizeReductionCandidateService _search = default!;

    [TestInitialize]
    public void Build()
    {
        _prowlarr = new FakeProwlarr();
        _catalog = new MediaCatalogService();

        var profile =
            new DriveProfile
            {
                Name = "Test Drive",
                MovieLibraryPath = Path.Combine(Folder, "movies"),
                TvLibraryPath = Path.Combine(Folder, "tv")
            };

        _search =
            new SizeReductionCandidateService(
                new OneDriveSettings(profile),
                _catalog,
                _prowlarr,
                new ReleaseParser(),
                new SizeReductionCandidateCacheService());
    }

    private MediaAsset Film(string title, int year, long sizeBytes, string resolution = "1080P")
    {
        var asset =
            new MediaAsset
            {
                Title = title,
                Year = year,
                MediaType = MediaType.Movie,
                CanonicalPath = WriteFile($@"movies\{title} ({year})\{title}.mkv"),
                FileSizeBytes = sizeBytes,
                Resolution = resolution
            };

        _catalog.AddAsset(asset);

        return asset;
    }

    [TestMethod]
    public async Task A_smaller_release_of_a_film_is_found_and_nothing_is_downloaded()
    {
        var film = Film("Alien", 1979, 20 * Gb);

        _prowlarr.Add("Alien 1979", "Alien.1979.1080p.BluRay.x265-GROUP", 8 * Gb);

        await _search.SearchAsync();

        var found = (await _search.GetFoundAsync()).Single();

        Assert.IsTrue(_search.LastSucceeded, _search.LastMessage);
        Assert.AreEqual(film.Id, found.MediaAssetId);
        Assert.AreEqual(12 * Gb, found.SavingsBytes);
        Assert.IsTrue(found.IsSameResolution);
        Assert.IsFalse(found.IsUpgrade);
        Assert.IsNotNull(_search.GetLastSearchUtc("Test Drive"));
    }

    [TestMethod]
    public async Task Releases_that_are_bigger_barely_smaller_another_year_or_have_no_size_are_left_out()
    {
        Film("Alien", 1979, 20 * Gb);
        Film("Scream", 1996, 20 * Gb);

        _prowlarr.Add("Alien 1979", "Alien.1979.1080p.Remux-GROUP", 30 * Gb);
        _prowlarr.Add("Alien 1979", "Alien.1979.1080p.BluRay.x264-GROUP", 20 * Gb - 100);
        _prowlarr.Add("Alien 1979", "Alien.2003.1080p.BluRay.x265-GROUP", 5 * Gb);
        _prowlarr.Add("Alien 1979", "Alien.1979.1080p.WEB.x265-GROUP", 0);
        _prowlarr.Add("Scream 1996", "Scream.1996.1080p.BluRay.x265-GROUP", 6 * Gb);

        await _search.SearchAsync();

        Assert.AreEqual(
            "Scream",
            (await _search.GetFoundAsync()).Single().AssetTitle);
    }

    [TestMethod]
    public async Task A_smaller_release_at_a_higher_resolution_is_marked_as_better_quality()
    {
        Film("Alien", 1979, 20 * Gb);

        _prowlarr.Add("Alien 1979", "Alien.1979.2160p.WEB.x265-GROUP", 12 * Gb);
        _prowlarr.Add("Alien 1979", "Alien.1979.720p.BluRay.x264-GROUP", 4 * Gb);

        await _search.SearchAsync();

        var found = await _search.GetFoundAsync();

        Assert.IsTrue(found.Single(release => release.CandidateResolution == "2160P").IsUpgrade);

        var lower = found.Single(release => release.CandidateResolution == "720P");

        Assert.IsFalse(lower.IsUpgrade);
        Assert.IsFalse(lower.IsSameResolution);
    }

    [TestMethod]
    public async Task One_search_covers_every_resolution_on_the_drive()
    {
        Film("Alien", 1979, 60 * Gb, "2160P");
        Film("Scream", 1996, 20 * Gb, "1080P");
        Film("Unprobed", 2001, 20 * Gb, "UNKNOWN");

        _prowlarr.Add("Alien 1979", "Alien.1979.2160p.WEB.x265-GROUP", 20 * Gb);
        _prowlarr.Add("Scream 1996", "Scream.1996.1080p.BluRay.x265-GROUP", 6 * Gb);

        await _search.SearchAsync("Test Drive");

        Assert.HasCount(2, await _search.GetFoundAsync());
        Assert.AreEqual(2, _prowlarr.Searches);
        Assert.AreEqual(1, await _search.CountUnscannedAssetsAsync());
    }

    [TestMethod]
    public async Task When_Prowlarr_cannot_be_reached_earlier_results_are_kept()
    {
        Film("Alien", 1979, 20 * Gb);

        _prowlarr.Add("Alien 1979", "Alien.1979.1080p.BluRay.x265-GROUP", 8 * Gb);

        await _search.SearchAsync();

        _prowlarr.IsReachable = false;

        await _search.SearchAsync();

        Assert.IsFalse(_search.LastSucceeded);
        Assert.IsFalse(_search.IsSearching);
        Assert.HasCount(1, await _search.GetFoundAsync());
    }

    [TestMethod]
    public async Task When_Prowlarr_stops_answering_part_way_earlier_results_are_kept()
    {
        Film("Alien", 1979, 20 * Gb);

        _prowlarr.Add("Alien 1979", "Alien.1979.1080p.BluRay.x265-GROUP", 8 * Gb);

        await _search.SearchAsync();

        // Still passes the connection test, then every search comes back empty.
        _prowlarr.Releases.Clear();

        await _search.SearchAsync();

        Assert.IsFalse(_search.LastSucceeded);
        Assert.HasCount(1, await _search.GetFoundAsync());
    }

    [TestMethod]
    public async Task A_stopped_search_keeps_earlier_results_and_can_be_run_again()
    {
        Film("Alien", 1979, 20 * Gb);
        Film("Scream", 1996, 20 * Gb);

        _prowlarr.Add("Alien 1979", "Alien.1979.1080p.BluRay.x265-GROUP", 8 * Gb);

        await _search.SearchAsync();

        _prowlarr.Add("Scream 1996", "Scream.1996.1080p.BluRay.x265-GROUP", 6 * Gb);
        _prowlarr.BeforeSearch = _search.Stop;

        await _search.SearchAsync();

        Assert.IsFalse(_search.LastSucceeded);
        Assert.IsFalse(_search.IsSearching);
        Assert.IsNull(_search.Progress);
        Assert.HasCount(1, await _search.GetFoundAsync());

        _prowlarr.BeforeSearch = null;

        await _search.SearchAsync();

        Assert.IsTrue(_search.LastSucceeded, _search.LastMessage);
        Assert.HasCount(2, await _search.GetFoundAsync());
    }
}
