using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IComicPromotionCandidateService
{
    Task<List<ComicPromotionCandidate>> GetCandidatesAsync(
        string? driveName = null,
        bool forceRefresh = false);
}
