using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Tests;

[TestClass]
public class AssetTorrentMatcherTests
{
    private readonly ReleaseParser _parser = new();

    private static TorrentRecord Torrent(string contentPath, long size) =>
        new()
        {
            Name = Path.GetFileName(contentPath),
            ContentPath = contentPath,
            SizeBytes = size,
            TorrentHash = Guid.NewGuid().ToString("N")
        };

    private MediaAsset Asset(string releaseName, long size)
    {
        var parsed = _parser.Parse(releaseName);

        return new MediaAsset
        {
            Title = parsed.NormalizedTitle,
            Year = parsed.Year,
            Resolution = parsed.Resolution,
            FileSizeBytes = size
        };
    }

    [TestMethod]
    public void An_asset_is_matched_to_the_torrent_with_the_same_title_and_year()
    {
        var matcher = new AssetTorrentMatcher(_parser);
        var asset = Asset("Metropolis.1927.1080p.BluRay.x264-GRP", 9_000);

        var torrents = new[]
        {
            Torrent(@"D:\dl\Metropolis.1927.1080p.BluRay.x264-GRP.mkv", 9_000),
            Torrent(@"D:\dl\Nosferatu.1922.1080p.BluRay.x264-GRP.mkv", 9_000),
            Torrent(@"D:\dl\Metropolis.2001.1080p.BluRay.x264-GRP.mkv", 9_000)
        };

        var matches = matcher.Match([asset], torrents);

        Assert.HasCount(1, matches);
        Assert.AreEqual(torrents[0].Id, matches.Single().TorrentId);
        Assert.AreEqual(100, matches.Single().Confidence);
    }

    [TestMethod]
    public void Matching_gives_the_same_answer_as_checking_every_pair_one_by_one()
    {
        var matcher = new AssetTorrentMatcher(_parser);

        string[] names =
        [
            "Metropolis.1927.1080p.BluRay.x264-GRP",
            "Metropolis.1927.2160p.WEB-DL.x265-OTHER",
            "Nosferatu.1922.720p.BluRay.x264-GRP",
            "The.General.1926.1080p.BluRay.x264-GRP",
            "Some.Show.S01E01.1080p.WEB.h264-GRP",
            "Some.Show.S01E02.1080p.WEB.h264-GRP",
            "Charade.1963.1080p.BluRay.x264-GRP"
        ];

        var assets = names.Select((name, index) => Asset(name, 1_000 + index * 250)).ToList();
        var torrents = names.Reverse().Select((name, index) => Torrent($@"D:\dl\{name}.mkv", 1_000 + index * 400)).ToList();

        // What the old code did, in the simplest possible form: every
        // asset against every torrent.
        var expected = new List<(Guid Asset, Guid Torrent)>();

        foreach (var asset in assets)
        {
            foreach (var torrent in torrents)
            {
                var parsed = _parser.Parse(Path.GetFileNameWithoutExtension(torrent.ContentPath));

                if (string.Equals(asset.Title, parsed.NormalizedTitle, StringComparison.OrdinalIgnoreCase) &&
                    !(asset.Year.HasValue && parsed.Year.HasValue && asset.Year != parsed.Year))
                {
                    expected.Add((asset.Id, torrent.Id));
                }
            }
        }

        var actual =
            matcher.Match(assets, torrents)
                .Select(match => (match.MediaAssetId, match.TorrentId))
                .ToList();

        Assert.IsNotEmpty(expected);
        CollectionAssert.AreEqual(expected, actual);
    }
}

[TestClass]
public class TorrentListTests : TempFolderTest
{
    [TestMethod]
    public void Reporting_the_same_torrent_again_does_not_add_it_twice()
    {
        var catalog = new MediaCatalogService();

        for (var scan = 0; scan < 3; scan++)
        {
            catalog.AddTorrent(new TorrentRecord { TorrentHash = "abc", Name = "one" });
            catalog.AddTorrent(new TorrentRecord { TorrentHash = "DEF", Name = "two" });
        }

        Assert.HasCount(2, catalog.GetTorrents());
    }
}

[TestClass]
public class FfprobeLocatorTests : TempFolderTest
{
    [TestMethod]
    public void A_configured_file_wins()
    {
        var configured = WriteFile(@"tools\ffprobe.exe");
        var onPath = WriteFile(@"path\ffprobe.exe");

        Assert.AreEqual(
            configured,
            FfprobeLocator.Search(configured, [Path.GetDirectoryName(onPath)!]));
    }

    [TestMethod]
    public void A_configured_folder_works_too()
    {
        var configured = WriteFile(@"tools\ffprobe.exe");

        Assert.AreEqual(
            configured,
            FfprobeLocator.Search(Path.GetDirectoryName(configured), []));
    }

    [TestMethod]
    public void Otherwise_the_first_folder_that_has_it_is_used()
    {
        var found = WriteFile(@"second\ffprobe.exe");

        Assert.AreEqual(
            found,
            FfprobeLocator.Search(
                null,
                [
                    Path.Combine(Folder, "first-is-empty"),
                    Path.Combine(Folder, "second"),
                    "not a real folder <>|"
                ]));
    }

    [TestMethod]
    public void A_wrong_configured_path_falls_back_to_searching()
    {
        var found = WriteFile(@"second\ffprobe.exe");

        Assert.AreEqual(
            found,
            FfprobeLocator.Search(
                Path.Combine(Folder, "moved-away"),
                [Path.Combine(Folder, "second")]));
    }

    [TestMethod]
    public void Nothing_found_is_null_not_an_error()
    {
        Assert.IsNull(FfprobeLocator.Search(null, [Path.Combine(Folder, "empty")]));
    }
}
