using System.Diagnostics;
using System.Text;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

[TestClass]
public class MusicAnalyzerTests
{
    private const string Root = @"M:\Music";

    private static MusicTrack Track(string relative, string? album, string? artist = "Low Tide", int? track = null, string? albumArtist = null, string? year = null, long size = 5_000_000)
    {
        var path = Path.Combine(Root, relative);

        return new MusicTrack
        {
            Path = path,
            Folder = Path.GetDirectoryName(path)!,
            Extension = Path.GetExtension(path),
            SizeBytes = size,
            LibraryRoot = Root,
            Tags = new MusicTags { Title = Path.GetFileNameWithoutExtension(path), Artist = artist, AlbumArtist = albumArtist, Album = album, Track = track, Year = year }
        };
    }

    private static List<MusicIssue> Analyze(IReadOnlyList<MusicTrack> tracks, IReadOnlyList<string>? others = null, IReadOnlyList<string>? empty = null) =>
        MusicAnalyzer.Analyze([Root], tracks, others ?? [], empty ?? []);

    [TestMethod]
    public void A_well_named_album_has_nothing_wrong()
    {
        var tracks = new[]
        {
            Track(@"Low Tide\Harbour Lights (2019)\01 Pier.flac", "Harbour Lights", track: 1),
            Track(@"Low Tide\Harbour Lights (2019)\02 Gulls.flac", "Harbour Lights", track: 2)
        };

        Assert.IsEmpty(Analyze(tracks));
    }

    [TestMethod]
    public void Artist_dash_album_folders_straight_in_the_library_are_fine()
    {
        var tracks = new[]
        {
            Track(@"Low Tide - Harbour Lights\01 Pier.mp3", "Harbour Lights", track: 1),
            Track(@"Low Tide - Harbour Lights\02 Gulls.mp3", "Harbour Lights", track: 2)
        };

        Assert.IsFalse(Analyze(tracks).Any(issue => issue.Kind == MusicIssueKind.Folder));
    }

    [TestMethod]
    public void Disc_folders_belong_to_their_album()
    {
        var tracks = new[]
        {
            Track(@"Low Tide\Harbour Lights\CD1\01 Pier.flac", "Harbour Lights", track: 1),
            Track(@"Low Tide\Harbour Lights\CD2\01 Gulls.flac", "Harbour Lights", track: 1)
        };

        tracks[1].Tags.Disc = 2;

        Assert.IsEmpty(Analyze(tracks));
    }

    [TestMethod]
    public void Tracks_loose_in_the_library_are_tidied_into_artist_and_album_folders()
    {
        var issue = Analyze([Track(@"Pier.mp3", "Harbour Lights", track: 1, year: "2019")]).Single(found => found.Kind == MusicIssueKind.Folder);

        Assert.AreEqual(Path.Combine(Root, "Low Tide", "Harbour Lights (2019)", "Pier.mp3"), issue.Moves.Single().To);
    }

    [TestMethod]
    public void Many_albums_in_one_folder_are_split_into_their_own_folders()
    {
        var tracks = new[]
        {
            Track(@"Downloads mix\a.mp3", "First", track: 1),
            Track(@"Downloads mix\b.mp3", "Second", track: 1),
            Track(@"Downloads mix\c.mp3", "Third", track: 1)
        };

        var issue = Analyze(tracks).Single(found => found.Kind == MusicIssueKind.Folder);

        StringAssert.Contains(issue.Title, "3 albums in one folder");
        Assert.HasCount(3, issue.Moves.Select(move => Path.GetDirectoryName(move.To)).Distinct());
    }

    [TestMethod]
    public void A_folder_that_doesnt_name_its_album_is_moved_to_a_proper_name()
    {
        var tracks = new[]
        {
            Track(@"Low Tide\New Folder (3)\01 Pier.flac", "Harbour Lights", track: 1),
            Track(@"Low Tide\New Folder (3)\02 Gulls.flac", "Harbour Lights", track: 2)
        };

        var issue = Analyze(tracks).Single(found => found.Kind == MusicIssueKind.Folder);

        StringAssert.Contains(issue.Title, "misnamed");
        Assert.AreEqual(Path.Combine(Root, "Low Tide", "Harbour Lights", "01 Pier.flac"), issue.Moves[0].To);
    }

    [TestMethod]
    public void The_same_album_in_two_folders_keeps_the_lossless_copy()
    {
        var tracks = new[]
        {
            Track(@"Low Tide\Harbour Lights\01.mp3", "Harbour Lights", track: 1),
            Track(@"Low Tide\Harbour Lights\02.mp3", "Harbour Lights", track: 2),
            Track(@"Low Tide\Harbour Lights [FLAC]\01.flac", "Harbour Lights", track: 1),
            Track(@"Low Tide\Harbour Lights [FLAC]\02.flac", "Harbour Lights", track: 2)
        };

        var issue = Analyze(tracks).Single(found => found.Kind == MusicIssueKind.Duplicate);

        Assert.AreEqual(Path.Combine(Root, "Low Tide", "Harbour Lights [FLAC]"), issue.Copies.Single(copy => copy.Suggested).Folder);
        Assert.IsTrue(issue.Paths.All(path => path.EndsWith(".mp3")), "The MP3 copy is the one to remove.");
    }

    [TestMethod]
    public void The_same_track_in_two_formats_keeps_the_better_one()
    {
        var tracks = new[]
        {
            Track(@"Low Tide\Harbour Lights\01 Pier.flac", "Harbour Lights", track: 1),
            Track(@"Low Tide\Harbour Lights\01 Pier.mp3", "Harbour Lights", track: 1)
        };

        var issue = Analyze(tracks).Single(found => found.Key.StartsWith("twice|"));

        Assert.AreEqual(".mp3", Path.GetExtension(issue.Paths.Single()));
    }

    [TestMethod]
    public void Missing_tags_gaps_and_mixed_album_artists_are_reported()
    {
        var tracks = new[]
        {
            Track(@"Low Tide\Harbour Lights\01.flac", "Harbour Lights", track: 1, albumArtist: "Low Tide"),
            Track(@"Low Tide\Harbour Lights\03.flac", "Harbour Lights", track: 3, albumArtist: "Low Tide & Friends"),
            Track(@"Low Tide\Harbour Lights\xx.flac", "Harbour Lights", track: null, albumArtist: "Low Tide")
        };

        var issue = Analyze(tracks).Single(found => found.Kind == MusicIssueKind.Tags);

        StringAssert.Contains(issue.Detail, "without track number");
        StringAssert.Contains(issue.Detail, "2 different album artists");
        StringAssert.Contains(issue.Detail, "track 2 missing");
    }

    [TestMethod]
    public void Leftovers_are_junk_but_a_cue_sheet_for_a_one_file_album_is_kept()
    {
        var tracks = new[]
        {
            Track(@"Low Tide\Harbour Lights\Harbour Lights.flac", "Harbour Lights", track: 1),
            Track(@"Low Tide\Other\01.flac", "Other", track: 1),
            Track(@"Low Tide\Other\02.flac", "Other", track: 2),
            Track(@"Low Tide\Other\03.flac", "Other", track: 3)
        };

        var others = new[]
        {
            Path.Combine(Root, @"Low Tide\Harbour Lights\Harbour Lights.cue"),
            Path.Combine(Root, @"Low Tide\Harbour Lights\rip.log"),
            Path.Combine(Root, @"Low Tide\Harbour Lights\folder.jpg"),
            Path.Combine(Root, @"Low Tide\Other\Other.cue")
        };

        var junk = Analyze(tracks, others).Where(found => found.Kind == MusicIssueKind.Junk).SelectMany(found => found.Paths).Select(Path.GetFileName).ToList();

        CollectionAssert.AreEquivalent(new[] { "rip.log", "Other.cue" }, junk);
    }

    [TestMethod]
    public void A_library_wrapped_in_a_Music_folder_is_organised_from_inside_it()
    {
        var folders = new[] { @"D:\music\Music\Low Tide", @"D:\music\Music\Gulls\First" };

        Assert.AreEqual(@"D:\music\Music", MusicAnalyzer.EffectiveRoot(@"D:\music", folders));
        Assert.AreEqual(@"D:\music", MusicAnalyzer.EffectiveRoot(@"D:\music", [@"D:\music\Low Tide\First"]), "An artist folder isn't a wrapper.");
    }

    [TestMethod]
    public void Folder_names_match_their_album_the_way_people_write_them()
    {
        Assert.IsTrue(MusicAnalyzer.NameMatches("Summer Mix 2021", "Summer Mashup Mix 2021"));
        Assert.IsTrue(MusicAnalyzer.NameMatches("Harbour Lights (Remastered) (Delu", "Harbour Lights (Remastered) (Deluxe Edition)"));
        Assert.IsTrue(MusicAnalyzer.NameMatches("2019 - Harbour Lights [FLAC]", "Harbour Lights"));
        Assert.IsTrue(MusicAnalyzer.NameMatches("Lighthouse Keeper", "Lighthouse Keeper (Original Game Soundtrack)"));
        Assert.IsFalse(MusicAnalyzer.NameMatches("New Folder (3)", "Harbour Lights"));
        Assert.IsFalse(MusicAnalyzer.NameMatches("Low Tide", "Harbour Lights"));
    }

    [TestMethod]
    public void An_album_in_a_Compilations_folder_is_where_it_belongs()
    {
        var tracks = new[]
        {
            Track(@"Compilations\Summer Mix 2021\01.mp3", "Summer Mashup Mix 2021", track: 1, albumArtist: "DJ Gull"),
            Track(@"Compilations\Summer Mix 2021\02.mp3", "Summer Mashup Mix 2021", track: 2, albumArtist: "DJ Gull")
        };

        Assert.IsFalse(Analyze(tracks).Any(issue => issue.Kind == MusicIssueKind.Folder));
    }

    [TestMethod]
    public void Unreadable_tracks_are_broken()
    {
        var track = Track(@"Low Tide\Harbour Lights\01.flac", "Harbour Lights", track: 1);
        track.Tags.Problem = "Doesn't start like a FLAC file";

        Assert.AreEqual("01.flac", Analyze([track]).Single(found => found.Kind == MusicIssueKind.Broken).Title);
    }
}

[TestClass]
public class MusicFileTests : TempFolderTest
{
    private static byte[] Flac(params string[] comments)
    {
        var vorbis = new MemoryStream();
        var vendor = Encoding.UTF8.GetBytes("test");

        vorbis.Write(BitConverter.GetBytes(vendor.Length));
        vorbis.Write(vendor);
        vorbis.Write(BitConverter.GetBytes(comments.Length));

        foreach (var comment in comments)
        {
            var bytes = Encoding.UTF8.GetBytes(comment);
            vorbis.Write(BitConverter.GetBytes(bytes.Length));
            vorbis.Write(bytes);
        }

        var file = new MemoryStream();
        file.Write("fLaC"u8);

        // A STREAMINFO block (34 bytes, contents unimportant here), then the comments as the last block.
        file.Write([0x00, 0, 0, 34]);
        file.Write(new byte[34]);

        var length = (int)vorbis.Length;
        file.Write([0x84, (byte)(length >> 16), (byte)(length >> 8), (byte)length]);
        file.Write(vorbis.ToArray());
        file.Write(new byte[40_000]);

        return file.ToArray();
    }

    private static byte[] Mp3(params (string Id, string Text)[] frames)
    {
        var body = new MemoryStream();

        foreach (var (id, text) in frames)
        {
            var value = Encoding.Latin1.GetBytes(text);
            var size = value.Length + 1;

            body.Write(Encoding.ASCII.GetBytes(id));
            body.Write([(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size, 0, 0, 0]);
            body.Write(value);
        }

        var tag = body.ToArray();
        var file = new MemoryStream();

        file.Write("ID3"u8);
        file.Write([3, 0, 0, (byte)((tag.Length >> 21) & 0x7F), (byte)((tag.Length >> 14) & 0x7F), (byte)((tag.Length >> 7) & 0x7F), (byte)(tag.Length & 0x7F)]);
        file.Write(tag);
        file.Write([0xFF, 0xFB, 0x90, 0x00]);
        file.Write(new byte[40_000]);

        return file.ToArray();
    }

    [TestMethod]
    public void FLAC_tags_are_read_without_reading_the_music()
    {
        var path = Path.Combine(Folder, "a.flac");
        File.WriteAllBytes(path, Flac("TITLE=Pier", "ARTIST=Low Tide", "ALBUMARTIST=Low Tide", "ALBUM=Harbour Lights", "TRACKNUMBER=3/10", "DISCNUMBER=1", "DATE=2019-05-01"));

        var tags = MusicTagReader.Read(path);

        Assert.IsNull(tags.Problem);
        Assert.AreEqual("Pier", tags.Title);
        Assert.AreEqual("Harbour Lights", tags.Album);
        Assert.AreEqual(3, tags.Track);
        Assert.AreEqual("2019", tags.Year);
    }

    [TestMethod]
    public void MP3_tags_are_read()
    {
        var path = Path.Combine(Folder, "a.mp3");
        File.WriteAllBytes(path, Mp3(("TIT2", "Gulls"), ("TPE1", "Low Tide"), ("TALB", "Harbour Lights"), ("TRCK", "02"), ("TYER", "2019")));

        var tags = MusicTagReader.Read(path);

        Assert.AreEqual("Gulls", tags.Title);
        Assert.AreEqual("Low Tide", tags.Artist);
        Assert.AreEqual(2, tags.Track);
    }

    [TestMethod]
    public void Empty_and_fake_files_are_flagged()
    {
        var empty = Path.Combine(Folder, "empty.flac");
        File.WriteAllBytes(empty, []);

        var fake = Path.Combine(Folder, "fake.flac");
        File.WriteAllBytes(fake, new byte[50_000]);

        StringAssert.Contains(MusicTagReader.Read(empty).Problem, "empty");
        StringAssert.Contains(MusicTagReader.Read(fake).Problem, "FLAC");
    }

    [TestMethod]
    public void Converted_files_go_where_the_mode_says()
    {
        var source = @"M:\Music\Low Tide\Harbour Lights\01 Pier.flac";

        Assert.AreEqual(@"M:\Music\Low Tide\Harbour Lights\01 Pier.mp3",
            MusicConvertService.TargetPath(new MusicConvertRequest(source, @"M:\Music", MusicConvertFormat.Mp3V0, MusicConvertMode.Replace, null)));

        Assert.AreEqual(@"P:\Phone\Low Tide\Harbour Lights\01 Pier.m4a",
            MusicConvertService.TargetPath(new MusicConvertRequest(source, @"M:\Music", MusicConvertFormat.Aac256, MusicConvertMode.OtherFolder, @"P:\Phone")));
    }

    [TestMethod]
    public void Lossy_tracks_are_never_made_lossless()
    {
        Assert.IsTrue(MusicFormats.CanConvert(".flac", MusicConvertFormat.Mp3V0));
        Assert.IsTrue(MusicFormats.CanConvert(".wav", MusicConvertFormat.Flac));
        Assert.IsFalse(MusicFormats.CanConvert(".mp3", MusicConvertFormat.Flac));
        Assert.IsFalse(MusicFormats.CanConvert(".mp3", MusicConvertFormat.Mp3Cbr320));
    }

    [TestMethod]
    public async Task A_FLAC_becomes_an_MP3_with_its_tags_and_the_original_goes_to_Quarantine()
    {
        if (FfprobeLocator.FindFfmpeg() is not { } ffmpeg)
        {
            Assert.Inconclusive("ffmpeg isn't installed here.");
            return;
        }

        var album = Path.Combine(Folder, "Music", "Low Tide", "Harbour Lights");
        Directory.CreateDirectory(album);
        var source = Path.Combine(album, "01 Pier.flac");

        // Three seconds of tone, tagged.
        using (var make = Process.Start(new ProcessStartInfo
               {
                   FileName = ffmpeg,
                   ArgumentList = { "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-metadata", "title=Pier", "-metadata", "album=Harbour Lights", "-metadata", "artist=Low Tide", "-metadata", "track=1", source },
                   UseShellExecute = false,
                   CreateNoWindow = true
               })!)
        {
            await make.WaitForExitAsync();
        }

        var quarantine = new FakeQuarantine();
        var settings = new DriveSettings(new SimplifiarrSettings());
        var converter = new MusicConvertService(quarantine, new HistoryService(settings));

        var result = await converter.ConvertAsync(new MusicConvertRequest(source, Path.Combine(Folder, "Music"), MusicConvertFormat.Mp3V0, MusicConvertMode.Replace, null));

        Assert.IsTrue(result.Succeeded, result.Message);

        var mp3 = Path.Combine(album, "01 Pier.mp3");
        var tags = MusicTagReader.Read(mp3);

        Assert.AreEqual("Pier", tags.Title);
        Assert.AreEqual("Harbour Lights", tags.Album);
        Assert.AreEqual(source, quarantine.Files.Single());
        Assert.IsEmpty(Directory.GetFiles(album, "*.simplifiarr-partial"));
        Assert.IsNull(MusicCleanupService.DecodeProblem(ffmpeg, mp3), "The MP3 plays through.");
    }

    [TestMethod]
    public async Task Tidying_tracks_loose_in_an_artist_folder_leaves_its_other_albums_alone()
    {
        var root = Path.Combine(Folder, "Music");
        var artist = Path.Combine(root, "Low Tide");
        var otherAlbum = Path.Combine(artist, "Older Album");
        Directory.CreateDirectory(otherAlbum);
        File.WriteAllText(Path.Combine(otherAlbum, "01 Old.flac"), "x");
        File.WriteAllText(Path.Combine(artist, "01 Pier.flac"), "x");
        File.WriteAllText(Path.Combine(artist, "artist.jpg"), "x");

        var tracks = new List<MusicTrack>
        {
            new() { Path = Path.Combine(artist, "01 Pier.flac"), Folder = artist, Extension = ".flac", LibraryRoot = root, Tags = new MusicTags { Title = "Pier", Artist = "Low Tide", Album = "Harbour Lights", Track = 1 } }
        };

        var issue = MusicAnalyzer.Analyze([root], tracks, [], []).Single(found => found.Kind == MusicIssueKind.Folder);

        StringAssert.StartsWith(issue.Key, "inartist|");

        var settings = new DriveSettings(new SimplifiarrSettings());
        await new MusicCleanupService(settings, new FakeQuarantine(), new HistoryService(settings)).TidyAsync(issue);

        Assert.IsTrue(File.Exists(Path.Combine(artist, "Harbour Lights", "01 Pier.flac")));
        Assert.IsTrue(File.Exists(Path.Combine(otherAlbum, "01 Old.flac")), "The other album stays where it is.");
        Assert.IsTrue(File.Exists(Path.Combine(artist, "artist.jpg")), "The artist's picture stays.");
    }

    [TestMethod]
    public async Task Tidying_moves_the_files_and_removes_the_old_folder()
    {
        var root = Path.Combine(Folder, "Music");
        var old = Path.Combine(root, "Low Tide", "New Folder");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "01 Pier.flac"), "x");
        File.WriteAllText(Path.Combine(old, "folder.jpg"), "x");

        var target = Path.Combine(root, "Low Tide", "Harbour Lights");

        var issue = new MusicIssue
        {
            Kind = MusicIssueKind.Folder,
            Key = "named|" + old,
            Folder = old,
            Moves = [new MusicMove(Path.Combine(old, "01 Pier.flac"), Path.Combine(target, "01 Pier.flac"))]
        };

        var settings = new DriveSettings(new SimplifiarrSettings());
        var service = new MusicCleanupService(settings, new FakeQuarantine(), new HistoryService(settings));

        var message = await service.TidyAsync(issue);

        StringAssert.Contains(message, "moved 2 file(s)");
        Assert.IsTrue(File.Exists(Path.Combine(target, "01 Pier.flac")));
        Assert.IsTrue(File.Exists(Path.Combine(target, "folder.jpg")), "The picture moves with the album.");
        Assert.IsFalse(Directory.Exists(old));
    }
}
