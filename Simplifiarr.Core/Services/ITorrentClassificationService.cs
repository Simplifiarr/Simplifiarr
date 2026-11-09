using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface ITorrentClassificationService
{
    TorrentAnalysis Analyze(
        TorrentRecord torrent,
        IReadOnlyList<DriveProfile> profiles);
}
