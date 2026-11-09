using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class PromotionPreviewService
    : IPromotionPreviewService
{
    public Task<PromotionPreview> CreateAsync(
        PromotionPlan plan)
    {
        return Task.FromResult(
            new PromotionPreview
            {
                SourcePath = plan.SourcePath,
                TargetDirectory = plan.TargetDirectory,
                TargetFilePath = plan.TargetFilePath,
                CanPromote = plan.CanPromote,
                Reason = plan.Reason,
                PlannedActions =
                [
                    "Create target directory if missing",
                    "Create hardlink",
                    "Verify hardlink creation"
                ]
            });
    }
}