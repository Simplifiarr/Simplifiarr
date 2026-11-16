using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

[TestClass]
public class LibraryLinkTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "simplifiarr-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Torrents", "Movies"));
        Directory.CreateDirectory(Path.Combine(_root, "Movies", "Night Shift"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Torrents => Path.Combine(_root, "Torrents");

    private string Movies => Path.Combine(_root, "Movies");

    [TestMethod]
    public void A_download_linked_into_a_renamed_library_folder_is_found()
    {
        if (!NativeHardlinks.IsSupported)
        {
            Assert.Inconclusive("Hardlinks are Windows-only here.");
        }

        var download = Path.Combine(Torrents, "Movies", "Night Shift - The Long Version.mp4");
        var library = Path.Combine(Movies, "Night Shift", "Night Shift - The Long Version.mp4");

        File.WriteAllText(download, "video");
        Assert.IsNull(NativeHardlinks.CreateHardlink(download, library));

        var names = NativeHardlinks.GetLinkPaths(download);

        Assert.HasCount(2, names);
        Assert.IsTrue(names.Contains(library, StringComparer.OrdinalIgnoreCase));

        Assert.AreEqual(
            library,
            MoviePromotionCandidateService.FindLibraryLink(download, [Movies], [Torrents]),
            ignoreCase: true);
    }

    [TestMethod]
    public void A_download_with_no_library_link_is_still_a_candidate()
    {
        var download = Path.Combine(Torrents, "Movies", "Harbour.mp4");

        File.WriteAllText(download, "video");

        Assert.IsNull(MoviePromotionCandidateService.FindLibraryLink(download, [Movies], [Torrents]));

        // A second name that is also in downloads (a cross-seed) doesn't count.
        if (NativeHardlinks.IsSupported)
        {
            Assert.IsNull(NativeHardlinks.CreateHardlink(download, Path.Combine(Torrents, "Harbour copy.mp4")));
            Assert.IsNull(MoviePromotionCandidateService.FindLibraryLink(download, [Movies], [Torrents]));
        }
    }

    [TestMethod]
    public void Downloads_inside_the_library_folder_are_not_mistaken_for_the_library()
    {
        if (!NativeHardlinks.IsSupported)
        {
            Assert.Inconclusive("Hardlinks are Windows-only here.");
        }

        // Library is the whole folder; downloads live inside it.
        var download = Path.Combine(Torrents, "Movies", "Baker's Dozen.mp4");

        File.WriteAllText(download, "video");
        Assert.IsNull(NativeHardlinks.CreateHardlink(download, Path.Combine(Torrents, "Baker's Dozen seed.mp4")));

        Assert.IsNull(MoviePromotionCandidateService.FindLibraryLink(download, [_root], [Torrents]));
    }
}
