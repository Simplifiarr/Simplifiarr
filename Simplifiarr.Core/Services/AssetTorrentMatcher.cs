using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class AssetTorrentMatcher
    : IAssetTorrentMatcher
{
    private readonly IReleaseParser _releaseParser;

    public AssetTorrentMatcher(
        IReleaseParser releaseParser)
    {
        _releaseParser = releaseParser;
    }

    public IReadOnlyCollection<AssetTorrentMatch> Match(
        IEnumerable<MediaAsset> assets,
        IEnumerable<TorrentRecord> torrents)
    {
        var matches =
            new List<AssetTorrentMatch>();

        // Work out each torrent's title once, and file the torrents under
        // it. This used to re-parse every torrent's name for every asset
        // (thousands of assets x dozens of torrents, on every drive of
        // every scan), which was where most of a scan's time went. The
        // matches found, and their order, are unchanged.
        var torrentsByTitle =
            new Dictionary<string, List<(TorrentRecord Torrent, ParsedRelease Parsed)>>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var torrent in torrents)
        {
            var parsed =
                _releaseParser.Parse(
                    Path.GetFileNameWithoutExtension(
                        torrent.ContentPath));

            var title = parsed.NormalizedTitle ?? string.Empty;

            if (!torrentsByTitle.TryGetValue(title, out var sameTitle))
            {
                sameTitle = [];
                torrentsByTitle[title] = sameTitle;
            }

            sameTitle.Add((torrent, parsed));
        }

        foreach (var asset in assets)
        {
            if (!torrentsByTitle.TryGetValue(
                    asset.Title ?? string.Empty,
                    out var candidates))
            {
                continue;
            }

            foreach (var (torrent, parsedTorrent) in candidates)
            {

                if (asset.Year.HasValue &&
                    parsedTorrent.Year.HasValue &&
                    asset.Year != parsedTorrent.Year)
                {
                    continue;
                }

                var sizeDifference =
                    Math.Abs(
                        asset.FileSizeBytes -
                        torrent.SizeBytes);

                var largerSize =
                    Math.Max(
                        asset.FileSizeBytes,
                        torrent.SizeBytes);

                var sizeRatio =
                    largerSize == 0
                        ? 0
                        : (double)Math.Min(
                                asset.FileSizeBytes,
                                torrent.SizeBytes)
                            / largerSize;

                var confidence =
                    50 +
                    (int)Math.Round(
                        sizeRatio * 50);

                var resolutionPenalty = 0;

                if (!string.IsNullOrWhiteSpace(asset.Resolution) &&
                    !string.IsNullOrWhiteSpace(parsedTorrent.Resolution) &&
                    !string.Equals(
                        asset.Resolution,
                        parsedTorrent.Resolution,
                        StringComparison.OrdinalIgnoreCase))
                {
                    resolutionPenalty = 20;
                    confidence -= resolutionPenalty;
                }

                var codecPenalty = 0;

                if (!string.IsNullOrWhiteSpace(asset.VideoCodec) &&
                    !string.IsNullOrWhiteSpace(parsedTorrent.Codec) &&
                    !string.Equals(
                        asset.VideoCodec,
                        parsedTorrent.Codec,
                        StringComparison.OrdinalIgnoreCase))
                {
                    codecPenalty = 10;
                    confidence -= codecPenalty;
                }

                confidence =
                    Math.Max(0, confidence);


                matches.Add(
                    new AssetTorrentMatch
                    {
                        MediaAssetId = asset.Id,
                        AssetTitle = asset.Title ?? string.Empty,

                        TorrentId = torrent.Id,
                        TorrentName = torrent.Name,

                        MatchReason =
                            "Normalized Title Match",

                        Confidence = confidence,

                        AssetResolution =
                            asset.Resolution,

                        AssetCodec =
                            asset.VideoCodec,

                        TorrentResolution =
                            parsedTorrent.Resolution,

                        TorrentCodec =
                            parsedTorrent.Codec,

                        AssetSizeBytes =
                            asset.FileSizeBytes,

                        TorrentSizeBytes =
                            torrent.SizeBytes,

                        SizeDifferenceBytes =
                            sizeDifference,

                        ScoreExplanation =
                            $"Title=50 Size={(int)Math.Round(sizeRatio * 50)} ResolutionPenalty=" + (asset.Resolution != parsedTorrent.Resolution ? "20" : "0"),

                        Breakdown =
                            new ScoreBreakdown
                            {
                                TitlePoints = 50,
                                SizePoints =
                                    (int)Math.Round(
                                        sizeRatio * 50),
                                ResolutionPenalty =
                                    resolutionPenalty,

                                CodecPenalty =
                                    codecPenalty,
                                TotalScore = confidence
                            }
                    });
            }
        }

        return matches;
    }
}











