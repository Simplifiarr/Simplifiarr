using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IRadarrClient
{
    Task<List<RadarrMovieRecord>> GetMoviesAsync();

    Task<bool> TestConnectionAsync();
}
