using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

/// <summary>
/// Every test gets its own empty folder under the system temp directory,
/// and the app's data folder is pointed there too, so tests never touch a
/// real Simplifiarr install.
/// </summary>
public abstract class TempFolderTest
{
    protected string Folder { get; private set; } = string.Empty;

    [TestInitialize]
    public void CreateFolder()
    {
        Folder = Path.Combine(
            Path.GetTempPath(),
            "SimplifiarrTests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Folder);

        AppPaths.Configure(Path.Combine(Folder, "appdata"));
    }

    [TestCleanup]
    public void DeleteFolder()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch
        {
            // Leftover temp files are harmless.
        }
    }

    protected string WriteFile(string relativePath, string content = "data")
    {
        var path = Path.Combine(Folder, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return path;
    }
}

[TestClass]
public class HardlinkServiceTests : TempFolderTest
{
    private readonly HardlinkService _service = new();

    [TestMethod]
    public async Task A_plain_file_has_one_link()
    {
        var file = WriteFile("movie.mkv");

        Assert.AreEqual(1, await _service.GetHardlinkCountAsync(file));
    }

    [TestMethod]
    public async Task A_missing_file_has_no_link_count()
    {
        Assert.IsNull(
            await _service.GetHardlinkCountAsync(
                Path.Combine(Folder, "nope.mkv")));
    }

    [TestMethod]
    public async Task Creating_a_hardlink_makes_a_second_name_for_the_same_file()
    {
        var download = WriteFile(@"downloads\movie.mkv", "the bytes");
        var library = Path.Combine(Folder, "library", "Movie (2020)", "Movie (2020).mkv");

        var result = await _service.CreateHardlinkAsync(download, library);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual("the bytes", File.ReadAllText(library));
        Assert.AreEqual(2, await _service.GetHardlinkCountAsync(download));
        Assert.AreEqual(2, await _service.GetHardlinkCountAsync(library));
        Assert.IsTrue(await _service.AreHardlinkedAsync(download, library));
    }

    [TestMethod]
    public async Task A_copy_is_not_a_hardlink()
    {
        var original = WriteFile("a.mkv", "same bytes");
        var copy = WriteFile("b.mkv", "same bytes");

        Assert.IsFalse(await _service.AreHardlinkedAsync(original, copy));
    }

    [TestMethod]
    public async Task It_refuses_to_overwrite_an_existing_file()
    {
        var source = WriteFile("a.mkv");
        var target = WriteFile("b.mkv", "keep me");

        var result = await _service.CreateHardlinkAsync(source, target);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("keep me", File.ReadAllText(target));
    }

    [TestMethod]
    public async Task It_reports_a_missing_source()
    {
        var result =
            await _service.CreateHardlinkAsync(
                Path.Combine(Folder, "missing.mkv"),
                Path.Combine(Folder, "link.mkv"));

        Assert.IsFalse(result.Success);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [TestMethod]
    public async Task It_works_with_paths_longer_than_260_characters()
    {
        var deep = string.Join(
            Path.DirectorySeparatorChar,
            Enumerable.Repeat(new string('d', 50), 6));

        var source = WriteFile(Path.Combine(deep, "source.mkv"));
        var link = Path.Combine(Folder, deep, "link.mkv");

        Assert.IsGreaterThan(260, source.Length);

        var result = await _service.CreateHardlinkAsync(source, link);

        Assert.IsTrue(result.Success, result.ErrorMessage);
        Assert.AreEqual(2, await _service.GetHardlinkCountAsync(source));
        Assert.IsTrue(await _service.AreHardlinkedAsync(source, link));
    }
}

[TestClass]
public class JsonFileStoreTests : TempFolderTest
{
    [TestMethod]
    public void Saved_data_loads_back_the_same()
    {
        var path = Path.Combine(Folder, "sub", "items.json");

        Assert.IsTrue(JsonFileStore.Save(path, new List<string> { "a", "b" }));

        CollectionAssert.AreEqual(
            new List<string> { "a", "b" },
            JsonFileStore.Load<List<string>>(path));
    }

    [TestMethod]
    public void A_missing_file_loads_as_null()
    {
        Assert.IsNull(
            JsonFileStore.Load<List<string>>(
                Path.Combine(Folder, "missing.json")));
    }

    [TestMethod]
    public void A_corrupt_file_loads_as_null_instead_of_throwing()
    {
        var path = WriteFile("broken.json", "{ this is not json");

        Assert.IsNull(JsonFileStore.Load<List<string>>(path));
    }

    [TestMethod]
    public void Saving_leaves_no_temporary_file_behind()
    {
        var path = Path.Combine(Folder, "items.json");

        JsonFileStore.Save(path, new List<string> { "a" });

        Assert.IsFalse(File.Exists(path + ".tmp"));
    }

    [TestMethod]
    public void Files_written_indented_by_older_versions_still_load()
    {
        var path = WriteFile("old.json", "[\r\n  \"a\",\r\n  \"b\"\r\n]");

        Assert.HasCount(2, JsonFileStore.Load<List<string>>(path)!);
    }
}

[TestClass]
public class AppPathsTests : TempFolderTest
{
    [TestMethod]
    public void The_old_PromotionCache_folder_is_renamed_to_Data()
    {
        var root = Path.Combine(Folder, "upgrade");

        Directory.CreateDirectory(Path.Combine(root, "PromotionCache"));
        File.WriteAllText(Path.Combine(root, "PromotionCache", "media-catalog.json"), "{}");

        AppPaths.Configure(root);

        Assert.IsTrue(File.Exists(AppPaths.DataFile("media-catalog.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "PromotionCache")));
    }

    [TestMethod]
    public void Every_location_sits_under_the_configured_folder()
    {
        var root = Path.Combine(Folder, "second-install");

        AppPaths.Configure(root);

        foreach (var path in new[]
                 {
                     AppPaths.SettingsFile,
                     AppPaths.DataDirectory,
                     AppPaths.LogsDirectory,
                     AppPaths.DefaultQuarantinePath,
                     AppPaths.DefaultHistoryFile,
                     AppPaths.DefaultCrossSeedTorrentPath
                 })
        {
            Assert.IsTrue(PathContainment.IsPathUnderRoot(path, root), path);
        }

        Assert.IsTrue(
            PathContainment.IsPathUnderRoot(
                new SimplifiarrSettings().QuarantinePath,
                root));
    }
}

[TestClass]
public class MediaCatalogServiceTests : TempFolderTest
{
    [TestMethod]
    public void Assets_are_found_by_path_regardless_of_case()
    {
        var catalog = new MediaCatalogService();
        var file = WriteFile(@"library\Movie (2020)\Movie (2020).mkv");
        var asset = new MediaAsset { Title = "Movie", CanonicalPath = file };

        catalog.AddAsset(asset);

        Assert.AreSame(asset, catalog.FindAssetByPath(file.ToUpperInvariant()));
        Assert.AreSame(asset, catalog.GetAsset(asset.Id));
        Assert.IsNull(catalog.FindAssetByPath(Path.Combine(Folder, "other.mkv")));
    }

    [TestMethod]
    public void Removed_assets_can_no_longer_be_found()
    {
        var catalog = new MediaCatalogService();
        var keep = WriteFile(@"library\keep.mkv");
        var drop = WriteFile(@"library\drop.mkv");
        var kept = new MediaAsset { Title = "Keep", CanonicalPath = keep };
        var dropped = new MediaAsset { Title = "Drop", CanonicalPath = drop };

        catalog.AddAsset(kept);
        catalog.AddAsset(dropped);

        catalog.RemoveAssetsUnderPathExcept(
            Path.Combine(Folder, "library"),
            [keep]);

        Assert.AreSame(kept, catalog.FindAssetByPath(keep));
        Assert.IsNull(catalog.FindAssetByPath(drop));
        Assert.IsNull(catalog.GetAsset(dropped.Id));
    }

    [TestMethod]
    public void The_catalog_survives_a_restart()
    {
        var file = WriteFile(@"library\movie.mkv");
        var first = new MediaCatalogService();
        var asset = new MediaAsset { Title = "Movie", CanonicalPath = file };

        first.AddAsset(asset);
        first.SaveToDisk();

        var second = new MediaCatalogService();

        Assert.AreEqual(asset.Id, second.FindAssetByPath(file)?.Id);
    }
}

[TestClass]
public class PathContainmentTests
{
    [TestMethod]
    [DataRow(@"C:\Media\Movies\A\a.mkv", @"C:\Media\Movies", true)]
    [DataRow(@"C:\Media\Movies", @"C:\Media\Movies\", true)]
    [DataRow(@"c:\media\movies\a.mkv", @"C:\Media\Movies", true)]
    [DataRow(@"C:\Media\Movies2\a.mkv", @"C:\Media\Movies", false)]
    [DataRow(@"C:\Media\a.mkv", @"C:\Media\Movies", false)]
    [DataRow(@"C:\Media\Movies\a.mkv", "", false)]
    [DataRow("", @"C:\Media\Movies", false)]
    public void IsPathUnderRoot(string path, string root, bool expected)
    {
        Assert.AreEqual(expected, PathContainment.IsPathUnderRoot(path, root));
    }
}

[TestClass]
public class SampleFileDetectorTests
{
    private static FileRecord Video(string fullPath, long megabytes) =>
        new()
        {
            FullPath = fullPath,
            FileName = Path.GetFileName(fullPath),
            Extension = Path.GetExtension(fullPath),
            FileType = FileType.Video,
            SizeBytes = megabytes * 1024 * 1024
        };

    [TestMethod]
    [DataRow(@"D:\dl\Movie.2020.1080p\sample.mkv", 40)]
    [DataRow(@"D:\dl\Movie.2020.1080p\movie.2020-sample.mkv", 40)]
    [DataRow(@"D:\dl\Movie.2020.1080p\Sample\movie.2020.mkv", 40)]
    public void Recognises_samples(string path, long megabytes)
    {
        Assert.IsTrue(SampleFileDetector.IsSample(Video(path, megabytes)));
    }

    [TestMethod]
    public void A_full_length_film_with_sample_in_its_title_is_not_a_sample()
    {
        Assert.IsFalse(
            SampleFileDetector.IsSample(
                Video(@"D:\dl\Sample.Size.2019.1080p\Sample.Size.2019.1080p.mkv", 6000)));
    }

    [TestMethod]
    public void An_ordinary_film_is_not_a_sample()
    {
        Assert.IsFalse(
            SampleFileDetector.IsSample(
                Video(@"D:\dl\Movie.2020.1080p\Movie.2020.1080p.mkv", 6000)));
    }
}

[TestClass]
public class ReleaseParserTests
{
    private readonly ReleaseParser _parser = new();

    [TestMethod]
    public void Parses_a_movie_release()
    {
        var release = _parser.Parse("The.General.1926.1080p.BluRay.x264-GRP");

        Assert.IsFalse(release.IsTv);
        Assert.AreEqual(1926, release.Year);
        StringAssert.Contains(
            release.NormalizedTitle.ToUpperInvariant(),
            "GENERAL");
    }

    [TestMethod]
    public void Parses_a_tv_episode()
    {
        var release = _parser.Parse("Some.Show.S02E05.720p.WEB.h264-GRP");

        Assert.IsTrue(release.IsTv);
        Assert.AreEqual(2, release.Season);
        Assert.AreEqual(5, release.Episode);
        StringAssert.Contains(
            release.NormalizedTitle.ToUpperInvariant(),
            "SOME SHOW");
    }

    [TestMethod]
    public void The_same_film_in_two_qualities_gets_the_same_title_and_year()
    {
        var hd = _parser.Parse("Metropolis.1927.1080p.BluRay.x264-GRP");
        var uhd = _parser.Parse("Metropolis.1927.2160p.WEB-DL.x265-OTHER");

        Assert.AreEqual(hd.Year, uhd.Year);
        Assert.AreEqual(
            TitleNormalizer.NormalizeForMatch(hd.NormalizedTitle),
            TitleNormalizer.NormalizeForMatch(uhd.NormalizedTitle));
    }
}

[TestClass]
public class SizeFormatterTests
{
    [TestMethod]
    [DataRow(0L, "0 B")]
    [DataRow(1023L, "1023 B")]
    [DataRow(1024L, "1 KB")]
    [DataRow(1610612736L, "1.5 GB")]
    public void FormatBytes(long bytes, string expected)
    {
        Assert.AreEqual(expected, SizeFormatter.FormatBytes(bytes));
    }
}
