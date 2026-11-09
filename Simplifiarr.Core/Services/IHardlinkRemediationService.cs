using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IHardlinkRemediationService
{
    Task<bool> ExecuteAsync(
        HardlinkConversionPlan plan);
}

