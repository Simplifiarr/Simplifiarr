using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IMusicPromotionCandidateService
{
    Task<List<MusicPromotionCandidate>> GetCandidatesAsync(
        string? driveName = null,
        bool forceRefresh = false);
}
