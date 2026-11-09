using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IAssetMatchAnalysisService
{
    IReadOnlyCollection<AssetMatchAnalysis> Analyze(
        IEnumerable<MediaAsset> assets,
        IEnumerable<TorrentRecord> torrents,
        IEnumerable<AssetTorrentMatch> matches);
}
