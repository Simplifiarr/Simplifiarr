using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IMoviePromotionCandidateService
{
    Task<List<MoviePromotionCandidate>> GetCandidatesAsync(
        string? driveName = null,
        bool forceRefresh = false,
        MediaKind mediaKind = MediaKind.All);
}
