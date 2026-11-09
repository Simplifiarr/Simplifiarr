using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class PromotionExecuteService
    : IPromotionExecuteService
{
    private readonly IHardlinkService _hardlinkService;

    public PromotionExecuteService(
        IHardlinkService hardlinkService)
    {
        _hardlinkService = hardlinkService;
    }

    public async Task<PromotionExecutionResult> ExecuteAsync(
        PromotionPlan plan)
    {
        var result =
            new PromotionExecutionResult();

        if (!plan.CanPromote)
        {
            result.Reason =
                "Promotion plan is not promotable.";

            return result;
        }

        if (!File.Exists(plan.SourcePath))
        {
            result.Reason =
                "Source file does not exist.";

            return result;
        }

        if (string.IsNullOrWhiteSpace(plan.TargetFilePath))
        {
            result.Reason =
                "Target file path is missing.";

            return result;
        }

        if (File.Exists(plan.TargetFilePath))
        {
            result.Reason =
                "Target file already exists.";

            return result;
        }

        if (!string.IsNullOrWhiteSpace(plan.TargetDirectory) &&
            !Directory.Exists(plan.TargetDirectory))
        {
            Directory.CreateDirectory(
                plan.TargetDirectory);

            result.DirectoryCreated = true;
        }

        var hardlinkResult =
            await _hardlinkService.CreateHardlinkAsync(
                plan.SourcePath,
                plan.TargetFilePath);

        result.HardlinkCreated =
            hardlinkResult.Success;

        if (!result.HardlinkCreated)
        {
            result.Reason =
                string.IsNullOrWhiteSpace(hardlinkResult.ErrorMessage)
                    ? "Hardlink creation failed."
                    : $"Hardlink creation failed: {hardlinkResult.ErrorMessage}";

            return result;
        }

        var hardlinkCount =
            await _hardlinkService.GetHardlinkCountAsync(
                plan.TargetFilePath);

        result.VerificationPassed =
            hardlinkCount >= 2;

        result.Success =
            result.VerificationPassed;

        result.Reason =
            result.Success
                ? "Promotion completed successfully."
                : "Hardlink verification failed.";

        return result;
    }
}
