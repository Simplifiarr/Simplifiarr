using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface ISonarrClient
{
    Task<List<SonarrEpisodeRecord>> GetEpisodesAsync();

    Task<bool> TestConnectionAsync();
}
