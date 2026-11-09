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

        foreach (var asset in assets)
        {
            foreach (var torrent in torrents)
            {
                var parsedTorrent =
                    _releaseParser.Parse(
                        Path.GetFileNameWithoutExtension(
                            torrent.ContentPath));

                if (!string.Equals(
                        asset.Title,
                        parsedTorrent.NormalizedTitle,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

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
                        AssetTitle = asset.Title,

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











