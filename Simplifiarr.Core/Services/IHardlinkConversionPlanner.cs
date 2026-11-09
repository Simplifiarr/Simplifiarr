using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IHardlinkConversionPlanner
{
    Task<HardlinkConversionPlan?> CreatePlanAsync(
        DuplicateCandidate candidate);
}

