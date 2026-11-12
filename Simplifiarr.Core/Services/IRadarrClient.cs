using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IRadarrClient
{
    Task<List<RadarrMovieRecord>> GetMoviesAsync();

    Task<bool> TestConnectionAsync();

    /// <summary>
    /// Radarr's own reading of a release name, and the film it belongs to
    /// if Radarr has it. Null when Radarr isn't set up, can't be reached or
    /// can't read the name.
    /// </summary>
    Task<ArrIdentity?> ParseAsync(string releaseName);

    /// <summary>Radarr's movie folder format, or null if Radarr can't be reached.</summary>
    Task<ArrFolderFormats?> GetFolderFormatsAsync();
}
