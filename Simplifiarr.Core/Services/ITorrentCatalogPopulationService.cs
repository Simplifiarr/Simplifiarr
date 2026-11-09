using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface ITorrentCatalogPopulationService
{
    Task<int> PopulateAsync(
        IEnumerable<TorrentRecord> torrents);
}
