using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class RemediationPreviewService : IRemediationPreviewService
{
    public Task<RemediationPreview> CreatePreviewAsync(
        HardlinkConversionPlan plan)
    {
        var preview = new RemediationPreview
        {
            FileName = plan.FileName,

            LibraryPath = plan.LibraryPath,
            DownloadPath = plan.DownloadPath,

            ExpectedSavings = plan.SizeFormatted,

            CanConvert = plan.CanConvert,

            Reason = plan.Reason
        };

        if (plan.LibraryFileExists)
        {
            preview.SafetyChecksPassed.Add(
                "Library file exists");
        }

        if (plan.DownloadFileExists)
        {
            preview.SafetyChecksPassed.Add(
                "Download file exists");
        }

        if (plan.SameVolume)
        {
            preview.SafetyChecksPassed.Add(
                $"Files are on the same volume ({plan.LibraryRoot})");
        }

        if (plan.CanConvert)
        {
            preview.PlannedActions.Add(
                "Remove existing library copy");

            preview.PlannedActions.Add(
                "Create hardlink pointing to download file");

            preview.PlannedActions.Add(
                "Verify hardlink creation");

            preview.PlannedActions.Add(
                "Expected storage savings will be reclaimed");
        }

        return Task.FromResult(preview);
    }
}

