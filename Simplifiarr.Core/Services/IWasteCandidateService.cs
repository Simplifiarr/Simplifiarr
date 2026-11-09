using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IWasteCandidateService
{
    Task<List<WasteCandidate>> GetCandidatesAsync(
        string? driveName = null,
        bool forceRefresh = false,
        MediaKind mediaKind = MediaKind.All);
}
