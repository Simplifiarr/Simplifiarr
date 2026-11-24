using System.Diagnostics;
using System.Text.Json;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

[TestClass]
public class SplitAlbumTests
{
    private const string Root = @"M:\Music";

    private static MusicTrack Track(string relative, string title, int track, int? disc = null) =>
        new()
        {
            Path = Path.Combine(Root, relative),
            Folder = Path.GetDirectoryName(Path.Combine(Root, relative))!,
            Extension = Path.GetExtension(relative),
            SizeBytes = 5_000_000,
            LibraryRoot = Root,
            Tags = new MusicTags { Title = title, Artist = "Low Tide", Album = "Harbour Lights", Track = track, Disc = disc }
        };

    [TestMethod]
    public void A_bonus_disc_in_its_own_folder_is_merged_not_called_a_duplicate()
    {
        var tracks = new[]
        {
            Track(@"Low Tide\Harbour Lights\01.mp3", "Pier", 1, 1),
            Track(@"Low Tide\Harbour Lights\02.mp3", "Gulls", 2, 1),
            Track(@"Low Tide\Harbour Lights\03.mp3", "Tide", 3, 1),
            Track(@"Low Tide\Harbour Lights (bonus)\01.mp3", "Pier (live)", 1, 3),
            Track(@"Low Tide\Harbour Lights (bonus)\02.mp3", "Gulls (live)", 2, 3)
        };

        var issues = MusicAnalyzer.Analyze([Root], tracks, [], []);

        Assert.IsFalse(issues.Any(issue => issue.Kind == MusicIssueKind.Duplicate));

        var split = issues.Single(issue => issue.Key.StartsWith("split|"));

        StringAssert.Contains(split.Title, "split across 2 folders");
        Assert.IsTrue(split.Moves.All(move => move.To.StartsWith(Path.Combine(Root, @"Low Tide\Harbour Lights\CD3"))), "Disc 3 gets its own folder.");
        Assert.IsEmpty(split.Paths);
    }

    [TestMethod]
    public void A_track_in_both_halves_of_a_split_album_is_the_extra_copy()
    {
        var tracks = new[]
        {
            Track(@"Low Tide\Harbour Lights\01.mp3", "Pier", 1),
            Track(@"Low Tide\Harbour Lights\02.mp3", "Gulls", 2),
            Track(@"Low Tide\Harbour Lights\06.mp3", "[untitled]", 6),
            Track(@"Low Tide\Harbour Lights 2\03.mp3", "Tide", 3),
            Track(@"Low Tide\Harbour Lights 2\06.mp3", "(Untitled)", 6)
        };

        var split = MusicAnalyzer.Analyze([Root], tracks, [], []).Single(issue => issue.Key.StartsWith("split|"));

        Assert.AreEqual(Path.Combine(Root, @"Low Tide\Harbour Lights 2\06.mp3"), split.Paths.Single());
        Assert.AreEqual(Path.Combine(Root, @"Low Tide\Harbour Lights\03.mp3"), split.Moves.Single().To);
    }

    [TestMethod]
    public void Copies_with_the_same_tracks_are_still_duplicates()
    {
        var tracks = new[]
        {
            Track(@"Low Tide\Harbour Lights\01.flac", "Pier", 1),
            Track(@"Low Tide\Harbour Lights\02.flac", "Gulls", 2),
            Track(@"Low Tide\Harbour Lights MP3\01.mp3", "Pier", 1),
            Track(@"Low Tide\Harbour Lights MP3\02.mp3", "Gulls", 2)
        };

        var issues = MusicAnalyzer.Analyze([Root], tracks, [], []);

        Assert.IsTrue(issues.Any(issue => issue.Kind == MusicIssueKind.Duplicate && issue.Key.StartsWith("album|")));
        Assert.IsFalse(issues.Any(issue => issue.Key.StartsWith("split|")));
    }
}

[TestClass]
public class MusicBrainzMatcherTests
{
    private static MusicTrack Track(string file, string? title, int? track = null) =>
        new()
        {
            Path = Path.Combine(@"M:\Music\Low Tide\Harbour Lights", file),
            Folder = @"M:\Music\Low Tide\Harbour Lights",
            Extension = Path.GetExtension(file),
            Tags = new MusicTags { Title = title, Artist = "Low tide", Album = "Harbour Lights", Track = track }
        };

    private static readonly MbRelease Release =
        new("r1", "Harbour Lights", "Low Tide", "2019", "GB", null,
        [
            new MbMedium(1, "CD", [new MbTrack(1, "Pier", "Low Tide"), new MbTrack(2, "Gulls", "Low Tide"), new MbTrack(3, "Tide (Remastered)", "Low Tide")]),
            new MbMedium(2, "CD", [new MbTrack(1, "Pier (live)", "Low Tide")])
        ]);

    [TestMethod]
    public void Tracks_are_matched_by_title_then_by_number()
    {
        var tracks = new[]
        {
            Track("01 Pier.mp3", "Pier", 1),
            Track("02.mp3", null, 2),
            Track("03 Tide.mp3", "Tide", 3),
            Track("99 Hidden.mp3", "Hidden Track", 99)
        };

        var match = MusicBrainzMatcher.Match(tracks, Release);

        Assert.HasCount(3, match.Tracks);
        Assert.AreEqual("99 Hidden.mp3", Path.GetFileName(match.Unmatched.Single()));

        var untitled = match.Tracks.Single(track => track.Path.EndsWith("02.mp3"));
        Assert.AreEqual("Gulls", untitled.NewTags.Title);
        Assert.IsTrue(untitled.Changes.Any(change => change.Field == "Title" && change.Before is null));

        var tide = match.Tracks.Single(track => track.Path.EndsWith("03 Tide.mp3"));
        Assert.AreEqual("Tide (Remastered)", tide.NewTags.Title);
        Assert.AreEqual(1, tide.NewTags.Disc, "A two-disc release sets the disc.");
        Assert.IsTrue(tide.Changes.Any(change => change.Field == "Artist"), "\"Low tide\" becomes MusicBrainz's \"Low Tide\".");
    }

    [TestMethod]
    public void A_demo_or_live_version_is_never_matched_to_the_album_track()
    {
        var tracks = new[]
        {
            Track("01 Pier (Writing Session).mp3", "Pier (Writing Session)", 1),
            Track("02 Gulls (live).mp3", "Gulls (Live)", 2),
            Track("03 Pier.mp3", "Pier", 3)
        };

        var match = MusicBrainzMatcher.Match(tracks, Release);

        Assert.AreEqual("03 Pier.mp3", Path.GetFileName(match.Tracks.Single().Path), "Only the real Pier matches.");
        Assert.HasCount(2, match.Unmatched);
        Assert.IsFalse(match.FitsWholeFolder, "A third of the folder can't be renamed on its own.");
    }

    [TestMethod]
    public void A_release_is_read_from_MusicBrainz_json()
    {
        var json =
            """
            {"id":"abc","title":"Harbour Lights","date":"2019-03-01","country":"GB",
             "artist-credit":[{"name":"Low Tide","joinphrase":" & "},{"name":"Gulls"}],
             "media":[{"position":1,"format":"CD","tracks":[{"position":1,"title":"Pier"},{"position":2,"title":"Gulls","artist-credit":[{"name":"Gulls"}]}]}]}
            """;

        var release = MusicBrainzClient.Parse(JsonDocument.Parse(json).RootElement);

        Assert.AreEqual("Low Tide & Gulls", release.Artist);
        Assert.AreEqual("2019", release.Year);
        Assert.AreEqual(2, release.TrackCount);
        Assert.AreEqual("Low Tide & Gulls", release.Media[0].Tracks[0].Artist, "A track without its own credit is the release's.");
        Assert.AreEqual("Gulls", release.Media[0].Tracks[1].Artist);
    }
}

[TestClass]
public class MusicTagWriterTests : TempFolderTest
{
    private async Task<string> MakeTrack(string name)
    {
        if (FfprobeLocator.FindFfmpeg() is not { } ffmpeg)
        {
            Assert.Inconclusive("ffmpeg isn't installed here.");
        }

        var path = Path.Combine(Folder, name);

        using var make = Process.Start(new ProcessStartInfo
        {
            FileName = FfprobeLocator.FindFfmpeg()!,
            ArgumentList = { "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-metadata", "title=Old title", path },
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        await make.WaitForExitAsync();

        return path;
    }

    [TestMethod]
    [DataRow("a.flac")]
    [DataRow("a.mp3")]
    public async Task Tags_are_rewritten_without_touching_the_music(string name)
    {
        var path = await MakeTrack(name);
        var size = new FileInfo(path).Length;

        var problem =
            await MusicTagWriter.WriteAsync(path, new MusicTags { Title = "Pier", Artist = "Low Tide", AlbumArtist = "Low Tide", Album = "Harbour Lights", Track = 1, Disc = 2, Year = "2019" });

        Assert.IsNull(problem, problem);

        var tags = MusicTagReader.Read(path);

        Assert.AreEqual("Pier", tags.Title);
        Assert.AreEqual("Low Tide", tags.AlbumArtist);
        Assert.AreEqual(1, tags.Track);
        Assert.AreEqual(2, tags.Disc);
        Assert.AreEqual("2019", tags.Year);
        Assert.IsLessThan(64 * 1024, Math.Abs(new FileInfo(path).Length - size), "Only the tags changed.");
        Assert.IsEmpty(Directory.GetFiles(Folder, "*.simplifiarr-partial"));
    }

    [TestMethod]
    public async Task A_seeding_track_is_never_rewritten()
    {
        if (!NativeHardlinks.IsSupported)
        {
            Assert.Inconclusive("Hardlinks are Windows-only here.");
        }

        var path = await MakeTrack("seed.flac");
        Assert.IsNull(NativeHardlinks.CreateHardlink(path, Path.Combine(Folder, "seed-copy.flac")));

        var before = File.ReadAllBytes(path);
        var problem = await MusicTagWriter.WriteAsync(path, new MusicTags { Title = "New" });

        StringAssert.Contains(problem, "seeding");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }
}
