using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IDuplicateCandidateService
{
    Task<List<DuplicateCandidate>> FindVideoCandidatesAsync(
        string libraryPath,
        string downloadPath,
        bool forceRefresh = false);
}

