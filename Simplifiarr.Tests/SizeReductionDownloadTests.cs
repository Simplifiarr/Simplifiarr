using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

[TestClass]
public class SizeReductionDownloadTests : TempFolderTest
{
    private const string Url = "http://indexer.test/harbour-720p";

    private FakeTorrentClient _client = default!;
    private FakeProwlarr _prowlarr = default!;
    private FakeArrPaths _arr = default!;
    private SimplifiarrSettings _settings = default!;
    private SizeReductionDownloadService _service = default!;

    private string _oldFile = default!;
    private string _downloads = default!;
    private byte[] _newContent = default!;
    private readonly List<string> _recycled = [];

    [TestInitialize]
    public void Build()
    {
        var library = Path.Combine(Folder, "Movies");
        _downloads = Path.Combine(Folder, "Torrents", "Movies");

        Directory.CreateDirectory(Path.Combine(library, "Harbour (2019)"));
        Directory.CreateDirectory(_downloads);

        _oldFile = Path.Combine(library, "Harbour (2019)", "Harbour (2019).mkv");
        File.WriteAllBytes(_oldFile, new byte[90_000]);

        _newContent = new byte[30_000];
        new Random(4).NextBytes(_newContent);

        _settings =
            new SimplifiarrSettings
            {
                DriveProfiles =
                [
                    new DriveProfile
                    {
                        Name = "D",
                        DownloadPath = Path.Combine(Folder, "Torrents"),
                        MovieDownloadPath = _downloads,
                        MovieLibraryPath = library
                    }
                ]
            };

        var settings = new DriveSettings(_settings);

        _client = new FakeTorrentClient();
        _prowlarr = new FakeProwlarr();
        _prowlarr.Torrents[Url] = TestTorrent.For(_newContent, "Harbour.2019.720p.mkv", "https://tracker.test/announce");
        _arr = new FakeArrPaths { KnownFolder = Path.GetDirectoryName(_oldFile) };

        _service =
            new SizeReductionDownloadService(settings, _prowlarr, _client, _arr, new HistoryService(settings))
            {
                RemoveOldFile = path =>
                {
                    _recycled.Add(path);
                    File.Delete(path);
                    return true;
                }
            };
    }

    private SizeReductionCandidate Candidate() =>
        new()
        {
            CanonicalPath = _oldFile,
            AssetSizeBytes = 90_000,
            Candidate = new ProwlarrSearchResult { Title = "Harbour.2019.720p", DownloadUrl = Url, SizeBytes = 30_000 }
        };

    /// <summary>What qBittorrent would have once the download is done.</summary>
    private void FinishDownload()
    {
        var hash = BencodeTorrentReader.TryParse(_prowlarr.Torrents[Url])!.InfoHashHex!;

        File.WriteAllBytes(Path.Combine(_downloads, "Harbour.2019.720p.mkv"), _newContent);

        _client.Torrents.Add(
            new TorrentRecord { TorrentHash = hash, Name = "Harbour.2019.720p.mkv", SavePath = _downloads, Progress = 1, Status = TorrentStatus.Seeding });

        _client.Files[hash] = [(new TorrentContentFile("Harbour.2019.720p.mkv", _newContent.Length), _newContent)];
    }

    [TestMethod]
    public async Task Download_starts_it_tagged_in_the_movie_downloads_folder_on_the_same_drive()
    {
        Assert.IsNull(await _service.StartAsync(Candidate()));

        var added = _client.Downloads.Single();

        Assert.AreEqual(_downloads, added.SavePath);
        Assert.AreEqual(SizeReductionDownloadService.Tag, added.Tag);
        Assert.AreEqual(SizeReductionDownloadStatus.Downloading, _service.Downloads.Single().Status);

        StringAssert.Contains(await _service.StartAsync(Candidate()), "already downloading");
    }

    [TestMethod]
    public async Task A_finished_download_waits_for_Swap_now_then_replaces_the_library_file()
    {
        await _service.StartAsync(Candidate());
        FinishDownload();

        await _service.CheckAsync();

        var download = _service.Downloads.Single();

        Assert.AreEqual(SizeReductionDownloadStatus.ReadyToSwap, download.Status);
        Assert.IsTrue(File.Exists(_oldFile), "Nothing changes until Swap now.");

        Assert.IsNull(await _service.SwapAsync(download.Id));

        var newFile = Path.Combine(Path.GetDirectoryName(_oldFile)!, "Harbour.2019.720p.mkv");

        Assert.IsTrue(File.Exists(newFile));
        Assert.IsFalse(File.Exists(_oldFile));
        CollectionAssert.AreEqual(new[] { _oldFile }, _recycled);
        Assert.AreEqual(SizeReductionDownloadStatus.Swapped, _service.Downloads.Single().Status);
        CollectionAssert.AreEqual(new[] { 7 }, _arr.Rescans);

        if (NativeHardlinks.IsSupported)
        {
            Assert.AreEqual(2, NativeHardlinks.GetIdentity(newFile)?.LinkCount, "Linked, so it keeps seeding.");
        }
    }

    [TestMethod]
    public async Task An_old_file_that_is_also_a_download_counts_nothing_as_saved()
    {
        // Radarr hardlinked the old file in from a download that is still on disk.
        var oldDownload = Path.Combine(Folder, "Torrents", "Movies", "Harbour.2019.1080p", "Harbour.2019.1080p.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(oldDownload)!);
        Assert.IsNull(NativeHardlinks.CreateHardlink(_oldFile, oldDownload));

        await _service.StartAsync(Candidate());
        FinishDownload();
        await _service.CheckAsync();

        var download = _service.Downloads.Single();

        Assert.IsNull(await _service.SwapAsync(download.Id));

        download = _service.Downloads.Single();

        Assert.IsFalse(File.Exists(_oldFile));
        Assert.IsEmpty(_recycled, "Not sent to the Recycle Bin, where it would be one more hidden name.");
        Assert.IsTrue(File.Exists(oldDownload), "The download keeps the old bytes.");
        Assert.AreEqual(0, download.SavedBytes);
        StringAssert.Contains(download.Message, "no space is freed");

        var record = (await new HistoryService(new DriveSettings(_settings)).GetHistoryAsync()).Single(entry => entry.ActionType == HistoryActionType.SizeReduced);
        Assert.AreEqual(0, record.SizeBytes);
    }

    [TestMethod]
    public async Task With_automatic_swapping_it_swaps_on_its_own()
    {
        _settings.SizeReductionAutoSwap = true;

        await _service.StartAsync(Candidate());
        FinishDownload();

        await _service.CheckAsync();

        Assert.AreEqual(SizeReductionDownloadStatus.Swapped, _service.Downloads.Single().Status);
        Assert.IsFalse(File.Exists(_oldFile));
    }

    [TestMethod]
    public async Task A_release_offered_only_as_a_magnet_link_is_added_by_its_magnet()
    {
        const string hash = "c12fe1c06bba254a9dc9f519b335aa7c1367a88a";

        _prowlarr.Magnets[Url] = $"magnet:?xt=urn:btih:{hash.ToUpperInvariant()}&dn=Harbour";

        Assert.IsNull(await _service.StartAsync(Candidate()));

        Assert.AreEqual(_downloads, _client.MagnetDownloads.Single().SavePath);
        Assert.AreEqual(hash, _service.Downloads.Single().TorrentHash);
        Assert.AreEqual(30_000, _service.Downloads.Single().ReleaseBytes, "Size from the indexer.");
    }

    [TestMethod]
    public void Magnet_hashes_are_read_as_hex_or_base32()
    {
        const string hex = "c12fe1c06bba254a9dc9f519b335aa7c1367a88a";

        // The same 20 bytes in base32.
        var base32 = ToBase32(Convert.FromHexString(hex));

        Assert.AreEqual(hex, SizeReductionDownloadService.MagnetHash($"magnet:?xt=urn:btih:{hex}"));
        Assert.AreEqual(hex, SizeReductionDownloadService.MagnetHash($"magnet:?dn=x&xt=urn:btih:{base32}&tr=y"));
        Assert.IsNull(SizeReductionDownloadService.MagnetHash("magnet:?dn=nothing"));
    }

    private static string ToBase32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var text = new System.Text.StringBuilder();
        int buffer = 0, bits = 0;

        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;

            while (bits >= 5)
            {
                text.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        return text.ToString();
    }

    [TestMethod]
    public async Task Cancelling_removes_the_torrent_and_its_files_and_leaves_your_file()
    {
        await _service.StartAsync(Candidate());
        FinishDownload();
        _client.Torrents.Single().Progress = 0.3;

        await _service.CheckAsync();

        var download = _service.Downloads.Single();

        Assert.IsNotNull(download.Live, "The swarm numbers are kept for the page.");
        Assert.IsNull(await _service.CancelAsync(download.Id));

        Assert.AreEqual((download.TorrentHash, true), _client.Removed.Single());
        Assert.AreEqual(SizeReductionDownloadStatus.Cancelled, _service.Downloads.Single().Status);
        Assert.IsTrue(File.Exists(_oldFile));
    }

    [TestMethod]
    public async Task A_swapped_download_cant_be_cancelled()
    {
        await _service.StartAsync(Candidate());
        FinishDownload();
        await _service.CheckAsync();
        await _service.SwapAsync(_service.Downloads.Single().Id);

        Assert.IsNotNull(await _service.CancelAsync(_service.Downloads.Single().Id));
        Assert.IsEmpty(_client.Removed, "Its file is in the library now.");
    }

    [TestMethod]
    public async Task An_unfinished_download_is_never_swapped()
    {
        await _service.StartAsync(Candidate());
        FinishDownload();
        _client.Torrents.Single().Progress = 0.4;

        await _service.CheckAsync();

        Assert.AreEqual(SizeReductionDownloadStatus.Downloading, _service.Downloads.Single().Status);
        Assert.IsNotNull(await _service.SwapAsync(_service.Downloads.Single().Id));
        Assert.IsTrue(File.Exists(_oldFile));
    }
}
