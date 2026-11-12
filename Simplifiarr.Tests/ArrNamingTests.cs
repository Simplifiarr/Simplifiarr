using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

internal sealed class FakeSonarr : ISonarrClient
{
    public Dictionary<string, ArrIdentity> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ArrFolderFormats? Formats { get; set; } = new() { SeasonFolderFormat = "Season {season}" };

    public int Asked { get; private set; }

    public Task<List<SonarrEpisodeRecord>> GetEpisodesAsync() => Task.FromResult(new List<SonarrEpisodeRecord>());

    public Task<bool> TestConnectionAsync() => Task.FromResult(Formats is not null);

    public Task<ArrIdentity?> ParseAsync(string releaseName)
    {
        Asked++;

        return Task.FromResult(Answers.TryGetValue(releaseName, out var answer) ? answer : null);
    }

    public Task<ArrFolderFormats?> GetFolderFormatsAsync() => Task.FromResult(Formats);
}

internal sealed class FakeRadarr : IRadarrClient
{
    public Dictionary<string, ArrIdentity> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ArrFolderFormats? Formats { get; set; } = new();

    public Task<List<RadarrMovieRecord>> GetMoviesAsync() => Task.FromResult(new List<RadarrMovieRecord>());

    public Task<bool> TestConnectionAsync() => Task.FromResult(Formats is not null);

    public Task<ArrIdentity?> ParseAsync(string releaseName) =>
        Task.FromResult(Answers.TryGetValue(releaseName, out var answer) ? answer : null);

    public Task<ArrFolderFormats?> GetFolderFormatsAsync() => Task.FromResult(Formats);
}

[TestClass]
public class ArrFolderNamerTests
{
    [TestMethod]
    public void Season_folders_follow_the_format()
    {
        Assert.AreEqual("Season 1", ArrFolderNamer.Season("Season {season}", 1));
        Assert.AreEqual("Season 01", ArrFolderNamer.Season("Season {season:00}", 1));
        Assert.AreEqual("Specials", ArrFolderNamer.Season("Specials", 0));
    }

    [TestMethod]
    public void Show_folders_follow_the_format()
    {
        Assert.AreEqual("Night Shift", ArrFolderNamer.Series("{Series Title}", "Night Shift", 2019));
        Assert.AreEqual("Night Shift (2019)", ArrFolderNamer.Series("{Series TitleYear}", "Night Shift", 2019));
        Assert.AreEqual("Night Shift (2019) [tvdbid-123]", ArrFolderNamer.Series("{Series TitleYear} [tvdbid-{TvdbId}]", "Night Shift", 2019, tvdbId: 123));
        Assert.AreEqual("Night.Shift", ArrFolderNamer.Series("{Series.Title}", "Night Shift", null));
        Assert.AreEqual("Harbour, The", ArrFolderNamer.Series("{Series TitleThe}", "The Harbour", null));
    }

    [TestMethod]
    public void A_missing_id_leaves_no_empty_brackets()
    {
        Assert.AreEqual("Night Shift (2019)", ArrFolderNamer.Series("{Series TitleYear} [tvdbid-{TvdbId}]", "Night Shift", 2019));
    }

    [TestMethod]
    public void A_title_that_already_ends_in_its_year_does_not_get_it_twice()
    {
        Assert.AreEqual("Night Shift 2019", ArrFolderNamer.Series("{Series TitleYear}", "Night Shift 2019", 2019));
    }

    [TestMethod]
    public void Colons_are_replaced_the_way_Sonarr_does_by_default()
    {
        Assert.AreEqual("Night Shift - Origins", ArrFolderNamer.Series("{Series Title}", "Night Shift: Origins", null));
    }

    [TestMethod]
    public void Film_folders_follow_the_format()
    {
        Assert.AreEqual("The Harbour (1999)", ArrFolderNamer.Movie("{Movie Title} ({Release Year})", "The Harbour", 1999));
        Assert.AreEqual("The Harbour (1999) {tmdb-42}", ArrFolderNamer.Movie("{Movie TitleYear} {tmdb-{TmdbId}}", "The Harbour", 1999, tmdbId: 42));
    }
}

[TestClass]
public class ReleaseParserTitleTests
{
    private readonly ReleaseParser _parser = new();

    [TestMethod]
    public void A_year_right_before_the_season_stays_in_a_show_name()
    {
        var release = _parser.Parse("Night.Shift.and.Others.2019.S01.1080p.WEB-DL");

        Assert.IsTrue(release.IsTv);
        Assert.AreEqual("Night Shift and Others 2019", release.NormalizedTitle);
        Assert.AreEqual(1, release.Season);
    }

    [TestMethod]
    public void A_film_year_is_still_kept_apart()
    {
        var release = _parser.Parse("The.Harbour.1999.1080p.BluRay.x264");

        Assert.IsFalse(release.IsTv);
        Assert.AreEqual("The Harbour", release.NormalizedTitle);
        Assert.AreEqual(1999, release.Year);
    }
}

[TestClass]
public class ArrNamingServiceTests
{
    private const string Release = "Night.Shift.S01E02.1080p.WEB-DL";

    private readonly FakeSonarr _sonarr = new();
    private readonly FakeRadarr _radarr = new();

    private ArrNamingService Service() => new(_sonarr, _radarr);

    private static ParsedRelease Guess() =>
        new ReleaseParser().Parse(Release);

    [TestMethod]
    public async Task A_show_Sonarr_has_uses_its_title_and_folder()
    {
        _sonarr.Answers[Release] =
            new ArrIdentity { Source = "Sonarr", Title = "Night Shift (2024)", Year = 2024, Season = 1, Episode = 2, KnownFolder = @"X:\TV\Night Shift (2024)" };

        var release = await Service().IdentifyAsync(Guess());

        Assert.AreEqual("Sonarr", release.IdentifiedBy);
        Assert.AreEqual("Night Shift (2024)", release.NormalizedTitle);
        Assert.AreEqual(@"X:\TV\Night Shift (2024)", release.KnownFolder);
        Assert.AreEqual("Season 1", release.SeasonFolderName);
    }

    [TestMethod]
    public async Task A_show_Sonarr_does_not_have_still_follows_its_folder_formats()
    {
        _sonarr.Formats = new ArrFolderFormats { SeriesFolderFormat = "{Series TitleYear}", SeasonFolderFormat = "Season {season}" };
        _sonarr.Answers[Release] = new ArrIdentity { Source = "Sonarr", Title = "Night Shift", Season = 1, Episode = 2 };

        var release = await Service().IdentifyAsync(Guess());

        Assert.IsNull(release.KnownFolder);
        Assert.AreEqual("Night Shift", release.FolderName);
        Assert.AreEqual("Season 1", release.SeasonFolderName);
    }

    [TestMethod]
    public async Task Without_Sonarr_the_release_is_unchanged()
    {
        _sonarr.Formats = null;

        var guess = Guess();
        var release = await Service().IdentifyAsync(guess);

        Assert.AreSame(guess, release);
    }

    [TestMethod]
    public async Task Each_release_is_asked_about_once()
    {
        var service = Service();

        await service.IdentifyAsync(Guess());
        await service.IdentifyAsync(Guess());

        Assert.AreEqual(1, _sonarr.Asked);
    }
}

[TestClass]
public class LibraryTargetTests : TempFolderTest
{
    private LibraryTargetResolver _resolver = default!;
    private string _downloads = default!;
    private string _tv = default!;

    [TestInitialize]
    public void Build()
    {
        _downloads = Path.Combine(Folder, "Torrents");
        _tv = Path.Combine(Folder, "TV");

        Directory.CreateDirectory(_downloads);
        Directory.CreateDirectory(_tv);

        _resolver =
            new LibraryTargetResolver(
                new DriveSettings(
                    new SimplifiarrSettings
                    {
                        DriveProfiles = [new DriveProfile { Name = "D", DownloadPath = _downloads, TvLibraryPath = _tv }]
                    }),
                new FileScannerService());
    }

    private string Source => Path.Combine(_downloads, "Night.Shift.S01E02.mkv");

    private static ParsedRelease Episode() =>
        new() { OriginalName = "Night.Shift.S01E02", NormalizedTitle = "Night Shift", IsTv = true, Season = 1, Episode = 2 };

    [TestMethod]
    public async Task A_show_kept_on_another_drive_gets_no_target()
    {
        var release = Episode();

        release.IdentifiedBy = "Sonarr";
        release.KnownFolder = Path.Combine(Folder, "elsewhere", "Night Shift");

        var target = await _resolver.ResolveAsync(release, Source);

        Assert.AreEqual(release.KnownFolder, target.KeptElsewhere);
        Assert.AreEqual(string.Empty, target.TargetFilePath);
    }

    [TestMethod]
    public async Task A_known_show_goes_into_its_folder_and_existing_season_folder()
    {
        var showFolder = Path.Combine(_tv, "Night Shift (2024)");

        Directory.CreateDirectory(Path.Combine(showFolder, "Season 01"));

        var release = Episode();

        release.KnownFolder = showFolder;
        release.SeasonFolderName = "Season 1";

        var target = await _resolver.ResolveAsync(release, Source);

        Assert.AreEqual(Path.Combine(showFolder, "Season 01"), target.TargetDirectory, "The folder the show already has wins.");
    }

    [TestMethod]
    public async Task A_new_show_is_named_by_the_format()
    {
        var release = Episode();

        release.FolderName = "Night Shift (2024)";
        release.SeasonFolderName = "Season 1";

        var target = await _resolver.ResolveAsync(release, Source);

        Assert.AreEqual(Path.Combine(_tv, "Night Shift (2024)", "Season 1"), target.TargetDirectory);
    }

    [TestMethod]
    public async Task Without_Sonarr_the_old_naming_is_kept()
    {
        var target = await _resolver.ResolveAsync(Episode(), Source);

        Assert.AreEqual(Path.Combine(_tv, "Night Shift", "Season 01"), target.TargetDirectory);
    }
}
