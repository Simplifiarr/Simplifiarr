using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IAudiobookPromotionCandidateService
{
    Task<List<AudiobookPromotionCandidate>> GetCandidatesAsync(
        string? driveName = null,
        bool forceRefresh = false);
}
