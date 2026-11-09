using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IAssetTorrentMatcher
{
    IReadOnlyCollection<AssetTorrentMatch> Match(
        IEnumerable<MediaAsset> assets,
        IEnumerable<TorrentRecord> torrents);
}
