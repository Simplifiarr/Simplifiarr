using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class JunkRulesTests
{
    private static FileRecord File(string path, long size = 1000)
    {
        var extension = Path.GetExtension(path);

        return new FileRecord
        {
            FullPath = path,
            FileName = Path.GetFileName(path),
            Extension = extension,
            FileType = FileScannerService.FileTypeOf(extension),
            SizeBytes = size
        };
    }

    private static string? Why(string path, JunkRules rules, bool book = false) =>
        JunkFileScannerService.Classify(File(path), book, rules);

    [TestMethod]
    public void The_defaults_match_what_junk_always_meant()
    {
        var rules = new JunkRules();

        Assert.AreEqual("Junk file type (.sfv)", Why(@"D:\Movies\A (2020)\a.sfv", rules));
        Assert.AreEqual("Sample video file", Why(@"D:\Movies\A (2020)\a-sample.mkv", rules));
        Assert.AreEqual("Leftover file in a Sample folder", Why(@"D:\Movies\A (2020)\Sample\a.nfo", rules));
        Assert.IsNotNull(Why(@"D:\Movies\A (2020)\screen1.jpg", rules));

        Assert.IsNull(Why(@"D:\Movies\A (2020)\movie.nfo", rules));
        Assert.IsNull(Why(@"D:\Movies\A (2020)\poster.jpg", rules));
        Assert.IsNull(Why(@"D:\Movies\A (2020)\A (2020).mkv", rules, book: false));
        Assert.IsNull(Why(@"D:\Books\A\metadata.json", rules, book: true));
    }

    [TestMethod]
    public void Each_kind_can_be_turned_off()
    {
        var rules = new JunkRules
        {
            SampleVideos = false,
            SampleFolderLeftovers = false,
            UnrecognizedImages = false,
            Extensions = []
        };

        Assert.IsNull(Why(@"D:\Movies\A (2020)\a.sfv", rules));
        Assert.IsNull(Why(@"D:\Movies\A (2020)\a-sample.mkv", rules));
        Assert.IsNull(Why(@"D:\Movies\A (2020)\Sample\a.nfo", rules));
        Assert.IsNull(Why(@"D:\Movies\A (2020)\screen1.jpg", rules));
    }

    [TestMethod]
    public void Added_types_and_names_count_but_media_never_does()
    {
        var rules = new JunkRules
        {
            Extensions = [".nfo", "EXE", ".mkv"],
            JunkNames = ["RARBG*.txt", "*.website"]
        };

        Assert.AreEqual("Junk file type (.nfo)", Why(@"D:\Movies\A (2020)\movie.nfo", rules));
        Assert.AreEqual("Junk file type (.exe)", Why(@"D:\Movies\A (2020)\Setup.exe", rules));
        Assert.AreEqual("Name you marked as junk", Why(@"D:\Movies\A (2020)\RARBG_DO_NOT_MIRROR.txt", rules));
        Assert.AreEqual("Name you marked as junk", Why(@"D:\Movies\A (2020)\Visit.website", rules));

        // A media type in the list is ignored.
        Assert.IsNull(Why(@"D:\Movies\A (2020)\A (2020).mkv", rules));
    }

    [TestMethod]
    public void Never_junk_wins_over_everything()
    {
        var rules = new JunkRules { KeepNames = ["*.sfv", "sample.mkv"] };

        Assert.IsNull(Why(@"D:\Movies\A (2020)\a.sfv", rules));
        Assert.IsNull(Why(@"D:\Movies\A (2020)\Sample\sample.mkv", rules));
        Assert.IsNotNull(Why(@"D:\Movies\A (2020)\a.md5", rules));
    }

    [TestMethod]
    public void File_types_are_tidied_and_nonsense_is_refused()
    {
        Assert.AreEqual(".exe", JunkRules.NormalizeExtension("EXE"));
        Assert.AreEqual(".par2", JunkRules.NormalizeExtension(" *.PAR2 "));
        Assert.IsNull(JunkRules.NormalizeExtension("."));
        Assert.IsNull(JunkRules.NormalizeExtension("a b"));
        Assert.IsNull(JunkRules.NormalizeExtension(""));
    }

    [TestMethod]
    public void Name_patterns_use_star_and_question_mark()
    {
        Assert.IsTrue(JunkFileScannerService.NameMatches("RARBG.txt", "rarbg*.TXT"));
        Assert.IsTrue(JunkFileScannerService.NameMatches("a1.nfo", "a?.nfo"));
        Assert.IsFalse(JunkFileScannerService.NameMatches("a12.nfo", "a?.nfo"));
        Assert.IsFalse(JunkFileScannerService.NameMatches("movie.nfo.bak", "*.nfo"));
        Assert.IsFalse(JunkFileScannerService.NameMatches("anything", " "));
    }
}
