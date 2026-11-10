using System.Security.Cryptography;
using System.Text;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

/// <summary>
/// Writes real single-file .torrent files, with real piece hashes, for
/// files made up by a test.
/// </summary>
internal static class TestTorrent
{
    private const int PieceLength = 16384;

    public static byte[] For(byte[] content, string name, string tracker)
    {
        var pieces = new MemoryStream();

        for (var offset = 0; offset < content.Length; offset += PieceLength)
        {
            pieces.Write(
                SHA1.HashData(
                    content.AsSpan(offset, Math.Min(PieceLength, content.Length - offset))));
        }

        var torrent = new MemoryStream();

        void Text(string value) =>
            torrent.Write(Encoding.UTF8.GetBytes(value));

        void Word(string value) =>
            Text($"{Encoding.UTF8.GetByteCount(value)}:{value}");

        Text("d");
        Word("announce");
        Word(tracker);
        Word("info");
        Text("d");
        Word("length");
        Text($"i{content.Length}e");
        Word("name");
        Word(name);
        Word("piece length");
        Text($"i{PieceLength}e");
        Word("pieces");
        Text($"{pieces.Length}:");
        torrent.Write(pieces.ToArray());
        Text("ee");

        return torrent.ToArray();
    }
}

internal sealed class FakeQbittorrent : IQbittorrentScanner
{
    public List<TorrentRecord> Torrents { get; } = [];

    public bool IsReachable { get; set; } = true;

    /// <summary>Folder each added torrent was pointed at.</summary>
    public List<string> AddedTo { get; } = [];

    public Task<List<TorrentRecord>> GetTorrentsAsync() =>
        Task.FromResult(IsReachable ? Torrents.ToList() : []);

    public Task<List<TorrentRecord>?> TryGetTorrentsAsync() =>
        Task.FromResult(IsReachable ? Torrents.ToList() : null);

    public Task<byte[]?> GetTorrentExportBytesAsync(string torrentHash) =>
        Task.FromResult<byte[]?>(null);

    public Task<QbittorrentAddResult> AddTorrentAsync(byte[] torrentBytes, string savePath)
    {
        AddedTo.Add(savePath);

        return Task.FromResult(new QbittorrentAddResult { Success = true });
    }
}

[TestClass]
public class CrossSeedTests : TempFolderTest
{
    private const string FirstTracker = "https://tracker.first.test/announce";
    private const string SecondTracker = "https://announce.second.test/announce";

    private FakeQbittorrent _qbittorrent = default!;
    private FakeProwlarr _prowlarr = default!;
    private MediaCatalogService _catalog = default!;
    private TorrentReconstructionService _reconstruction = default!;
    private CrossSeedScanService _crossSeed = default!;
    private string _storage = default!;

    [TestInitialize]
    public void Build()
    {
        _qbittorrent = new FakeQbittorrent();
        _prowlarr = new FakeProwlarr();
        _catalog = new MediaCatalogService();

        var verification = new TorrentPieceVerificationService();

        _reconstruction =
            new TorrentReconstructionService(verification, new HardlinkService());

        _storage = new SimplifiarrSettings().CrossSeedTorrentStoragePath;

        _crossSeed =
            new CrossSeedScanService(
                _qbittorrent,
                _prowlarr,
                new ReleaseParser(),
                new FileScannerService(),
                _catalog,
                new WholeTorrentCandidateService(verification, _reconstruction),
                new OneDriveSettings(new DriveProfile { Name = "Test Drive" }));
    }

    private static byte[] Bytes(int seed, int length = 40000)
    {
        var bytes = new byte[length];

        new Random(seed).NextBytes(bytes);

        return bytes;
    }

    /// <summary>A file on disk that qBittorrent is seeding.</summary>
    private TorrentRecord Seeding(string fileName, byte[] content, string tracker = FirstTracker)
    {
        var path = Path.Combine(Folder, "downloads", fileName);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);

        var torrent =
            new TorrentRecord
            {
                TorrentHash = Guid.NewGuid().ToString("N"),
                Name = fileName,
                ContentPath = path,
                State = "uploading",
                SizeBytes = content.Length,
                Tracker = tracker
            };

        _qbittorrent.Torrents.Add(torrent);

        return torrent;
    }

    /// <summary>A release on an indexer; returns its .torrent.</summary>
    private byte[] OnIndexer(string name, byte[] content, string tracker = SecondTracker)
    {
        var torrent = TestTorrent.For(content, name, tracker);
        var url = "http://indexer.test/" + Guid.NewGuid().ToString("N");

        _prowlarr.Torrents[url] = torrent;

        _prowlarr.EveryQuery.Add(
            new ProwlarrSearchResult
            {
                Title = name,
                Indexer = "Test indexer",
                SizeBytes = content.Length,
                DownloadUrl = url
            });

        return torrent;
    }

    [TestMethod]
    public async Task The_same_file_under_another_name_on_another_tracker_is_a_match()
    {
        var content = Bytes(1);

        Seeding("Alien.1979.1080p.BluRay.x264-GROUP.mkv", content);
        OnIndexer("Alien (1979) 1080p.mkv", content);

        await _crossSeed.ScanAsync();

        Assert.IsTrue(_crossSeed.LastSucceeded, _crossSeed.LastMessage);

        var match = _crossSeed.Matches.Single();

        Assert.IsFalse(match.IsAlreadySeededOnSameTracker);
        Assert.HasCount(1, match.SeedingTorrents);
        Assert.IsFalse(_crossSeed.IsScanning);
        Assert.IsNull(_crossSeed.Progress);
    }

    [TestMethod]
    public async Task A_file_of_the_same_size_but_different_contents_is_not_a_match()
    {
        Seeding("Alien.1979.1080p.BluRay.x264-GROUP.mkv", Bytes(1));
        OnIndexer("Alien (1979) 1080p.mkv", Bytes(2));

        await _crossSeed.ScanAsync();

        Assert.IsTrue(_crossSeed.LastSucceeded, _crossSeed.LastMessage);
        Assert.IsEmpty(_crossSeed.Matches);
    }

    [TestMethod]
    public async Task A_torrent_already_in_qBittorrent_is_left_out()
    {
        var content = Bytes(1);
        var seeding = Seeding("Alien.1979.1080p.BluRay.x264-GROUP.mkv", content);
        var torrent = OnIndexer("Alien (1979) 1080p.mkv", content);

        seeding.InfoHashV1 = BencodeTorrentReader.TryParse(torrent)!.InfoHashHex!;

        await _crossSeed.ScanAsync();

        Assert.IsEmpty(_crossSeed.Matches);
        Assert.HasCount(1, _crossSeed.AlreadyInQbittorrent);
    }

    [TestMethod]
    public async Task A_match_on_a_tracker_already_seeded_to_is_marked_and_not_offered_as_new()
    {
        var content = Bytes(1);

        Seeding("Alien.1979.1080p.BluRay.x264-GROUP.mkv", content, "https://tracker.second.test/announce");
        OnIndexer("Alien (1979) 1080p.mkv", content, SecondTracker);

        await _crossSeed.ScanAsync();

        Assert.IsTrue(_crossSeed.Matches.Single().IsAlreadySeededOnSameTracker);
    }

    [TestMethod]
    public async Task Adding_a_match_hardlinks_the_file_under_the_new_name_and_hands_it_to_qBittorrent()
    {
        var content = Bytes(1);
        var seeding = Seeding("Alien.1979.1080p.BluRay.x264-GROUP.mkv", content);

        OnIndexer("Alien (1979) 1080p.mkv", content);

        await _crossSeed.ScanAsync();

        var summary = await _crossSeed.AddAsync(_crossSeed.Matches);

        Assert.AreEqual(new CrossSeedAddSummary(1, 0, 1, 0), summary);
        Assert.IsEmpty(_crossSeed.Matches);
        Assert.HasCount(1, _qbittorrent.AddedTo);

        var linked =
            Directory
                .EnumerateFiles(_storage, "Alien (1979) 1080p.mkv", SearchOption.AllDirectories)
                .Single();

        CollectionAssert.AreEqual(content, File.ReadAllBytes(linked));

        // A hardlink, not a copy: both names are the one file.
        Assert.AreEqual(2, await new HardlinkService().GetHardlinkCountAsync(seeding.ContentPath));

        // The original is untouched.
        CollectionAssert.AreEqual(content, File.ReadAllBytes(seeding.ContentPath));
    }

    [TestMethod]
    public async Task When_qBittorrent_cannot_be_reached_nothing_is_searched()
    {
        _qbittorrent.IsReachable = false;

        await _crossSeed.ScanAsync();

        Assert.IsFalse(_crossSeed.LastSucceeded);
        Assert.IsFalse(_crossSeed.IsScanning);
        Assert.AreEqual(0, _prowlarr.Searches);
    }

    [TestMethod]
    public async Task The_rebuilder_finds_a_renamed_library_file_and_links_it_under_the_torrents_name()
    {
        var content = Bytes(3);
        var libraryPath = Path.Combine(Folder, "movies", "Scream (1996)", "Scream (1996).mkv");

        Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
        File.WriteAllBytes(libraryPath, content);

        var wrongPath = Path.Combine(Folder, "movies", "Other (2001)", "Other (2001).mkv");

        Directory.CreateDirectory(Path.GetDirectoryName(wrongPath)!);
        File.WriteAllBytes(wrongPath, Bytes(4));

        var assets =
            new[] { wrongPath, libraryPath }
                .Select(path => new MediaAsset
                {
                    Title = Path.GetFileNameWithoutExtension(path),
                    MediaType = MediaType.Movie,
                    CanonicalPath = path,
                    FileSizeBytes = content.Length
                })
                .ToList();

        var metadata =
            BencodeTorrentReader.TryParse(
                TestTorrent.For(content, "Scream.1996.1080p.BluRay.x264-GROUP.mkv", FirstTracker))!;

        var result =
            await _reconstruction.ReconstructFromCatalogAsync(metadata, assets, _storage);

        Assert.IsTrue(result.Success, result.FailureReason);
        Assert.AreEqual(libraryPath, result.MatchedAsset!.CanonicalPath);
        Assert.AreEqual("Scream.1996.1080p.BluRay.x264-GROUP.mkv", Path.GetFileName(result.TargetPath));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(result.TargetPath!));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(libraryPath));
    }
}
