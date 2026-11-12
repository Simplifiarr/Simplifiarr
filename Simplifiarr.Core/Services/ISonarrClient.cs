using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface ISonarrClient
{
    Task<List<SonarrEpisodeRecord>> GetEpisodesAsync();

    Task<bool> TestConnectionAsync();

    /// <summary>
    /// Sonarr's own reading of a release name, and the show it belongs to
    /// if Sonarr has it. Null when Sonarr isn't set up, can't be reached or
    /// doesn't see an episode in the name.
    /// </summary>
    Task<ArrIdentity?> ParseAsync(string releaseName);

    /// <summary>Sonarr's folder formats, or null if Sonarr can't be reached.</summary>
    Task<ArrFolderFormats?> GetFolderFormatsAsync();
}
