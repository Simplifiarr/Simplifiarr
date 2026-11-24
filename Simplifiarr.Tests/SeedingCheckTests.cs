using System.Diagnostics;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

[TestClass]
public class SeedingCheckTests : TempFolderTest
{
    private string _library = string.Empty;
    private string _downloads = string.Empty;

    private (string Library, string Download) Linked(string name, string content = "music")
    {
        if (!NativeHardlinks.IsSupported)
        {
            Assert.Inconclusive("Hardlinks are Windows-only here.");
        }

        _library = Path.Combine(Folder, "Music");
        _downloads = Path.Combine(Folder, "Downloads", "Album");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_downloads);

        var library = Path.Combine(_library, name);
        var download = Path.Combine(_downloads, name);

        File.WriteAllText(download, content);
        Assert.IsNull(NativeHardlinks.CreateHardlink(download, library));

        return (library, download);
    }

    [TestMethod]
    public void A_hardlink_only_counts_as_seeding_when_a_torrent_uses_it()
    {
        var (library, _) = Linked("01.flac");

        Assert.AreEqual(SeedState.Seeding, SeedingCheck.From([(_downloads, "Album")]).Check(library).State);
        Assert.AreEqual(SeedState.LinkedNotSeeding, SeedingCheck.From([(Path.Combine(Folder, "Other"), "Other")]).Check(library).State);
        Assert.AreEqual(SeedState.Unknown, SeedingCheck.From(null).Check(library).State, "Without the client, play safe.");
        Assert.AreEqual(SeedState.NotLinked, SeedingCheck.From([]).Check(Path.Combine(Folder, "nothing.flac")).State);
    }

    [TestMethod]
    public async Task Tags_on_a_leftover_hardlink_are_written_into_the_same_file()
    {
        if (FfprobeLocator.FindFfmpeg() is not { } ffmpeg)
        {
            Assert.Inconclusive("ffmpeg isn't installed here.");
            return;
        }

        var (library, download) = Linked("01.flac");
        File.Delete(library);
        File.Delete(download);

        using (var make = Process.Start(new ProcessStartInfo
               {
                   FileName = ffmpeg,
                   ArgumentList = { "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=2", download },
                   UseShellExecute = false,
                   CreateNoWindow = true
               })!)
        {
            await make.WaitForExitAsync();
        }

        Assert.IsNull(NativeHardlinks.CreateHardlink(download, library));

        var problem = await MusicTagWriter.WriteAsync(library, new MusicTags { Title = "Pier" }, SeedingCheck.From([]));

        Assert.IsNull(problem, problem);
        Assert.AreEqual("Pier", MusicTagReader.Read(download).Title, "Both names are still one file, with the new tags.");
        Assert.AreEqual(2, NativeHardlinks.GetIdentity(library)!.Value.LinkCount);
    }

    [TestMethod]
    public void Album_names_lose_only_the_editions_at_the_end()
    {
        Assert.AreEqual("(Low) Tide Tables?", MusicAnalyzer.WithoutEdition("(Low) Tide Tables? (Remastered) (Deluxe Edition)"));
        Assert.AreEqual("Harbour Lights", MusicAnalyzer.WithoutEdition("Harbour Lights [Deluxe]"));
        Assert.AreEqual("(Untitled)", MusicAnalyzer.WithoutEdition("(Untitled)"));
    }

    [TestMethod]
    public void Untitled_tracks_are_not_lined_up_by_number_on_a_release_whose_titles_dont_match()
    {
        var release =
            new MbRelease("x", "Another Album", "Low Tide", "2016", null, null,
                [new MbMedium(1, "CD", [new MbTrack(1, "Something Else", "Low Tide"), new MbTrack(2, "Different", "Low Tide"), new MbTrack(3, "Nope", "Low Tide")])]);

        MusicTrack Track(string file, string? title, int track) =>
            new() { Path = @"M:\Music\" + file, Folder = @"M:\Music", Extension = ".mp3", Tags = new MusicTags { Title = title, Track = track } };

        var match = MusicBrainzMatcher.Match([Track("01.mp3", "Pier", 1), Track("02.mp3", "Gulls", 2), Track("03.mp3", null, 3)], release);

        Assert.IsEmpty(match.Tracks);
    }
}
