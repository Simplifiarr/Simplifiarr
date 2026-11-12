using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class DownloadCleanupTests : TempFolderTest
{
    private FakeQbittorrent _qbittorrent = default!;
    private DownloadCleanupService _service = default!;

    // Two "drives", each with its own downloads and library folders.
    private string _firstDownloads = default!;
    private string _firstMovies = default!;
    private string _secondMovies = default!;

    [TestInitialize]
    public void Build()
    {
        _firstDownloads = Path.Combine(Folder, "first", "Torrents");
        _firstMovies = Path.Combine(Folder, "first", "Movies");
        _secondMovies = Path.Combine(Folder, "second", "Movies");

        foreach (var folder in new[] { _firstDownloads, _firstMovies, _secondMovies })
        {
            Directory.CreateDirectory(folder);
        }

        var settings =
            new DriveSettings(
                new SimplifiarrSettings
                {
                    DriveProfiles =
                    [
                        new DriveProfile { Name = "First", DownloadPath = _firstDownloads, MovieLibraryPath = _firstMovies },
                        new DriveProfile { Name = "Second", DownloadPath = Path.Combine(Folder, "second", "Torrents"), MovieLibraryPath = _secondMovies }
                    ]
                });

        _qbittorrent = new FakeQbittorrent();

        _service =
            new DownloadCleanupService(
                settings,
                new FileInventoryCacheService(new FileScannerService()),
                _qbittorrent,
                new HardlinkService(),
                new HistoryService(settings));
    }

    private static string Write(string folder, string name, string content)
    {
        var path = Path.Combine(folder, name);

        File.WriteAllText(path, content);

        return path;
    }

    [TestMethod]
    public async Task A_finished_download_whose_library_copy_is_on_another_drive_is_found()
    {
        var download = Write(_firstDownloads, "Film.2002.720p.mkv", "the same film");
        var library = Write(_secondMovies, "Film (2002).mkv", "the same film");

        var candidate = (await _service.ScanAsync("First")).Candidates.Single();

        Assert.AreEqual(download, candidate.DownloadPath);
        Assert.AreEqual(library, candidate.LibraryPath);
        Assert.AreEqual(DownloadMatchKind.IdenticalCopy, candidate.MatchKind);
    }

    [TestMethod]
    public async Task A_download_on_another_drive_that_is_still_seeding_is_left_alone()
    {
        var download = Write(_firstDownloads, "Film.2002.720p.mkv", "the same film");

        Write(_secondMovies, "Film (2002).mkv", "the same film");

        _qbittorrent.Torrents.Add(new TorrentRecord { ContentPath = download });

        Assert.IsEmpty((await _service.ScanAsync("First")).Candidates);
    }

    [TestMethod]
    public async Task A_same_size_file_on_another_drive_with_different_content_is_not_a_match()
    {
        Write(_firstDownloads, "Film.2002.720p.mkv", "the same size A");
        Write(_secondMovies, "Film (2002).mkv", "the same size B");

        Assert.IsEmpty((await _service.ScanAsync("First")).Candidates);
    }
}
