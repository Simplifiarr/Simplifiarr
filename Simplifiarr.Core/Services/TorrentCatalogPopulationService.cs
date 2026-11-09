using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class TorrentCatalogPopulationService
    : ITorrentCatalogPopulationService
{
    private const int MatchThreshold = 80;

    private readonly IMediaCatalogService _catalogService;
    private readonly IAssetTorrentMatcher _assetTorrentMatcher;

    public TorrentCatalogPopulationService(
        IMediaCatalogService catalogService,
        IAssetTorrentMatcher assetTorrentMatcher)
    {
        _catalogService = catalogService;
        _assetTorrentMatcher = assetTorrentMatcher;
    }

    public Task<int> PopulateAsync(
        IEnumerable<TorrentRecord> torrents)
    {
        ArgumentNullException.ThrowIfNull(torrents);

        var torrentList =
            torrents.ToList();

        foreach (var torrent in torrentList)
        {
            _catalogService.AddTorrent(torrent);
        }

        var matches =
            _assetTorrentMatcher.Match(
                _catalogService.GetAssets(),
                torrentList);

        var existingSources =
            _catalogService.GetSources()
                .Where(source =>
                    source.SourceType == SourceType.Qbittorrent)
                .Select(source => new
                {
                    source.MediaAssetId,
                    source.ExternalId
                })
                .ToHashSet();

        foreach (var torrentMatches in matches
            .Where(match =>
                match.Confidence >= MatchThreshold)
            .GroupBy(match =>
                match.TorrentId))
        {
            var rankedMatches =
                torrentMatches
                    .OrderByDescending(match =>
                        match.Confidence)
                    .ToList();

            var bestMatch =
                rankedMatches[0];

            if (rankedMatches.Count > 1 &&
                rankedMatches[1].Confidence ==
                bestMatch.Confidence)
            {
                continue;
            }

            var torrent =
                torrentList.FirstOrDefault(item =>
                    item.Id == bestMatch.TorrentId);

            if (torrent is null ||
                string.IsNullOrWhiteSpace(
                    torrent.TorrentHash))
            {
                continue;
            }

            var sourceKey = new
            {
                bestMatch.MediaAssetId,
                ExternalId = torrent.TorrentHash
            };

            if (existingSources.Contains(sourceKey))
            {
                continue;
            }

            _catalogService.AddSource(
                new SourceRecord
                {
                    MediaAssetId =
                        bestMatch.MediaAssetId,

                    SourceType =
                        SourceType.Qbittorrent,

                    ExternalId =
                        torrent.TorrentHash
                });

            existingSources.Add(sourceKey);
        }

        return Task.FromResult(
            torrentList.Count);
    }
}
