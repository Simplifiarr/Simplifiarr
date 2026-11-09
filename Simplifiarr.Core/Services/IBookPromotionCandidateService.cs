using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IBookPromotionCandidateService
{
    Task<List<BookPromotionCandidate>> GetCandidatesAsync(
        string? driveName = null,
        bool forceRefresh = false);
}
