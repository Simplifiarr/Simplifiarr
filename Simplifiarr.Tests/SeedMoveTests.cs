using System.Text;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class SeedMoveTests : TempFolderTest
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    private FakeTorrentClient _qbittorrent = default!;
    private SeedMoveService _service = default!;
    private HardlinkService _hardlinks = default!;

    // Two "drives": the torrent seeds from the first, the library is on the second.
    private string _firstDownloads = default!;
    private string _firstTv = default!;
    private string _secondDownloads = default!;
    private string _secondTv = default!;

    private readonly List<string> _recycled = [];

    [TestInitialize]
    public void Build()
    {
        _firstDownloads = Path.Combine(Folder, "first", "Torrents");
        _firstTv = Path.Combine(Folder, "first", "TV");
        _secondDownloads = Path.Combine(Folder, "second", "Torrents");
        _secondTv = Path.Combine(Folder, "second", "TV");

        foreach (var folder in new[] { _firstDownloads, _firstTv, _secondDownloads, _secondTv })
        {
            Directory.CreateDirectory(folder);
        }

        var settings =
            new DriveSettings(
                new SimplifiarrSettings
                {
                    DriveProfiles =
                    [
                        new DriveProfile { Name = "First", DownloadPath = _firstDownloads, TvLibraryPath = _firstTv },
                        new DriveProfile { Name = "Second", DownloadPath = _secondDownloads, TvLibraryPath = _secondTv }
                    ]
                });

        _qbittorrent = new FakeTorrentClient();
        _hardlinks = new HardlinkService();

        _service =
            new SeedMoveService(
                settings,
                new FileInventoryCacheService(new FileScannerService()),
                _qbittorrent,
                _hardlinks,
                new HistoryService(settings))
            {
                PollInterval = TimeSpan.FromMilliseconds(1),
                SmallestMatchedFileBytes = 1,
                RemoveOldFile = path =>
                {
                    _recycled.Add(path);
                    File.Delete(path);
                    return true;
                }
            };
    }

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return path;
    }

    /// <summary>A finished torrent in the first drive's downloads\tv folder.</summary>
    private string Seed(params (string RelativePath, string Content)[] files)
    {
        var savePath = Path.Combine(_firstDownloads, "tv");

        foreach (var (relativePath, content) in files)
        {
            Write(Path.Combine(savePath, relativePath), content);
        }

        _qbittorrent.Torrents.Add(
            new TorrentRecord
            {
                TorrentHash = Hash,
                Name = "Night Shift S01",
                SavePath = savePath,
                State = "uploading",
                Status = TorrentStatus.Seeding,
                Progress = 1,
                Category = "tv",
                Tracker = "udp://tracker.example.org:1337/announce"
            });

        _qbittorrent.Files[Hash] =
            files
                .Select(file => (new TorrentContentFile(file.RelativePath, Encoding.UTF8.GetByteCount(file.Content)), Encoding.UTF8.GetBytes(file.Content)))
                .ToList();

        return savePath;
    }

    private async Task<SeedMoveCandidate> FindAsync()
    {
        await _service.ScanAsync("First");

        return _service.GetCandidates("First").Single();
    }

    [TestMethod]
    public async Task A_seed_whose_library_copy_is_on_another_drive_is_found()
    {
        Seed((@"Night Shift S01\E01.mkv", "episode one"));
        Write(Path.Combine(_secondTv, @"Night Shift\Season 1\E01.mkv"), "episode one");

        var candidate = await FindAsync();

        Assert.AreEqual("Second", candidate.TargetDriveName);
        Assert.AreEqual(Path.Combine(_secondDownloads, "tv"), candidate.TargetSavePath);
        Assert.AreEqual("tracker.example.org", candidate.TrackerHost);
        Assert.IsTrue(candidate.Files.Single().InLibrary);
        Assert.IsEmpty(_qbittorrent.Calls, "Finding changes nothing in qBittorrent.");
    }

    [TestMethod]
    public async Task A_seed_on_the_same_drive_as_its_library_copy_is_not_listed()
    {
        Seed((@"Night Shift S01\E01.mkv", "episode one"));
        Write(Path.Combine(_firstTv, @"Night Shift\Season 1\E01.mkv"), "episode one");

        await _service.ScanAsync("First");

        Assert.IsEmpty(_service.GetCandidates("First"));
    }

    [TestMethod]
    public async Task Moving_links_the_library_file_and_removes_the_old_copy()
    {
        var oldFolder = Seed((@"Night Shift S01\E01.mkv", "episode one"));
        var library = Write(Path.Combine(_secondTv, @"Night Shift\Season 1\E01.mkv"), "episode one");
        var oldFile = Path.Combine(oldFolder, @"Night Shift S01\E01.mkv");

        var result = await _service.MoveAsync(await FindAsync());

        Assert.IsTrue(result.Succeeded, result.Message);

        var torrent = _qbittorrent.Torrents.Single();
        var seed = Path.Combine(_secondDownloads, "tv", @"Night Shift S01\E01.mkv");

        Assert.AreEqual(Path.Combine(_secondDownloads, "tv"), torrent.SavePath);
        Assert.IsTrue(await _hardlinks.AreHardlinkedAsync(seed, library), "The seed and the library share one file.");
        Assert.IsFalse(File.Exists(oldFile), "The old copy is gone.");
        CollectionAssert.AreEqual(new[] { oldFile }, _recycled);
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(oldFile)), "The torrent's emptied folder is removed.");
        Assert.IsTrue(Directory.Exists(oldFolder), "The downloads folder itself stays.");
        Assert.AreEqual("stoppedUP", torrent.State, "It is left paused.");
        Assert.DoesNotContain("start", _qbittorrent.Calls);
        Assert.AreEqual(Encoding.UTF8.GetByteCount("episode one"), result.FreedBytes);
        Assert.IsEmpty(_service.GetCandidates("First"));
    }

    [TestMethod]
    public async Task A_file_the_library_does_not_have_is_moved_by_qbittorrent()
    {
        Seed(
            (@"Night Shift S01\E01.mkv", "episode one"),
            (@"Night Shift S01\sample.mkv", "a sample"));
        Write(Path.Combine(_secondTv, @"Night Shift\Season 1\E01.mkv"), "episode one");

        var candidate = await FindAsync();

        Assert.AreEqual(Encoding.UTF8.GetByteCount("a sample"), candidate.CopiedBytes);

        var result = await _service.MoveAsync(candidate);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsTrue(File.Exists(Path.Combine(_secondDownloads, "tv", @"Night Shift S01\sample.mkv")));
    }

    [TestMethod]
    public async Task When_the_check_fails_everything_is_put_back()
    {
        // Same size, different bytes: qBittorrent's check won't pass.
        var oldFolder = Seed((@"Night Shift S01\E01.mkv", "episode one"));
        var library = Write(Path.Combine(_secondTv, @"Night Shift\Season 1\E01.mkv"), "episode 1!!");
        var oldFile = Path.Combine(oldFolder, @"Night Shift S01\E01.mkv");

        var result = await _service.MoveAsync(await FindAsync());

        Assert.IsFalse(result.Succeeded);

        var torrent = _qbittorrent.Torrents.Single();

        Assert.AreEqual(oldFolder, torrent.SavePath, "The torrent points at its old files again.");
        Assert.AreEqual("episode one", File.ReadAllText(oldFile), "The old copy was never touched.");
        Assert.AreEqual("episode 1!!", File.ReadAllText(library), "Nor was the library's.");
        Assert.IsFalse(File.Exists(Path.Combine(_secondDownloads, "tv", @"Night Shift S01\E01.mkv")), "The link Simplifiarr made is removed.");
        Assert.IsEmpty(_recycled);
        Assert.HasCount(1, _service.GetCandidates("First"), "It stays on the list.");
    }

    [TestMethod]
    public async Task A_torrent_moved_since_the_scan_is_left_alone()
    {
        Seed((@"Night Shift S01\E01.mkv", "episode one"));
        Write(Path.Combine(_secondTv, @"Night Shift\Season 1\E01.mkv"), "episode one");

        var candidate = await FindAsync();

        _qbittorrent.Torrents.Single().SavePath = Path.Combine(_firstDownloads, "elsewhere");

        var result = await _service.MoveAsync(candidate);

        Assert.IsFalse(result.Succeeded);
        Assert.IsEmpty(_qbittorrent.Calls, "Nothing was asked of qBittorrent.");
    }

    [TestMethod]
    public async Task A_torrent_mostly_missing_from_the_library_is_not_listed()
    {
        Seed(
            (@"Night Shift S01\E01.mkv", "one"),
            (@"Night Shift S01\E02.mkv", "the second episode, which is longer"));
        Write(Path.Combine(_secondTv, @"Night Shift\Season 1\E01.mkv"), "one");

        await _service.ScanAsync("First");

        Assert.IsEmpty(_service.GetCandidates("First"));
    }

    [TestMethod]
    public async Task An_unfinished_torrent_is_not_listed()
    {
        Seed((@"Night Shift S01\E01.mkv", "episode one"));
        Write(Path.Combine(_secondTv, @"Night Shift\Season 1\E01.mkv"), "episode one");

        _qbittorrent.Torrents.Single().Progress = 0.5;

        await _service.ScanAsync("First");

        Assert.IsEmpty(_service.GetCandidates("First"));
    }
}
