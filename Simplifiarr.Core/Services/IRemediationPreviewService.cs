using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public interface IRemediationPreviewService
{
    Task<RemediationPreview> CreatePreviewAsync(
        HardlinkConversionPlan plan);
}

