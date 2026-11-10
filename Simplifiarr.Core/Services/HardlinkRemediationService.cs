using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

public class HardlinkRemediationService : IHardlinkRemediationService
{
    private readonly IHardlinkService _hardlinkService;
    private readonly IQuarantineService _quarantineService;
    private readonly IHistoryService _historyService;

    public HardlinkRemediationService(
        IHardlinkService hardlinkService,
        IQuarantineService quarantineService,
        IHistoryService historyService)
    {
        _hardlinkService = hardlinkService;
        _quarantineService = quarantineService;
        _historyService = historyService;
    }

    public async Task<bool> ExecuteAsync(
        HardlinkConversionPlan plan)
    {
        var fileName =
            Path.GetFileName(plan.LibraryPath);

        var sizeBytes =
            File.Exists(plan.LibraryPath)
                ? new FileInfo(plan.LibraryPath).Length
                : 0;

        if (!plan.CanConvert)
        {
            await AddConversionHistoryAsync(
                plan,
                string.Empty,
                sizeBytes,
                false,
                "Conversion plan is not marked as convertible.");

            return false;
        }

        // Last line of defence, whatever the plan says: never quarantine a
        // file in order to link it to itself.
        if (string.Equals(
                Path.GetFullPath(plan.LibraryPath),
                Path.GetFullPath(plan.DownloadPath),
                StringComparison.OrdinalIgnoreCase) ||
            await _hardlinkService.AreHardlinkedAsync(
                plan.LibraryPath,
                plan.DownloadPath))
        {
            await AddConversionHistoryAsync(
                plan,
                string.Empty,
                sizeBytes,
                false,
                "Nothing to convert: the library file and the download are already the same file.");

            return false;
        }

        if (!File.Exists(plan.LibraryPath))
        {
            await AddConversionHistoryAsync(
                plan,
                string.Empty,
                sizeBytes,
                false,
                "Library file does not exist.");

            return false;
        }

        if (!File.Exists(plan.DownloadPath))
        {
            await AddConversionHistoryAsync(
                plan,
                string.Empty,
                sizeBytes,
                false,
                "Download file does not exist.");

            return false;
        }

        string? quarantinePath = null;

        try
        {
            quarantinePath =
                await _quarantineService.QuarantineFileAsync(
                    plan.LibraryPath);

            if (string.IsNullOrWhiteSpace(quarantinePath))
            {
                await AddConversionHistoryAsync(
                    plan,
                    string.Empty,
                    sizeBytes,
                    false,
                    "Quarantine failed.");

                return false;
            }

            var created =
                await _hardlinkService.CreateHardlinkAsync(
                    plan.DownloadPath,
                    plan.LibraryPath);

            if (!created.Success)
            {
                if (File.Exists(quarantinePath) && !File.Exists(plan.LibraryPath))
                {
                    File.Move(quarantinePath, plan.LibraryPath);
                }

                var detail =
                    string.IsNullOrWhiteSpace(created.ErrorMessage)
                        ? string.Empty
                        : $" ({created.ErrorMessage})";

                await AddConversionHistoryAsync(
                    plan,
                    quarantinePath,
                    sizeBytes,
                    false,
                    $"Hardlink creation failed{detail}. Original file was restored from Quarantine.");

                return false;
            }

            var hardlinkCount =
                await _hardlinkService.GetHardlinkCountAsync(
                    plan.LibraryPath);

            if (hardlinkCount is > 1)
            {
                await AddConversionHistoryAsync(
                    plan,
                    quarantinePath,
                    sizeBytes,
                    true,
                    "Conversion succeeded. Original duplicate moved to Quarantine and hardlink verified.");

                return true;
            }

            if (File.Exists(plan.LibraryPath))
            {
                File.Delete(plan.LibraryPath);
            }

            if (File.Exists(quarantinePath))
            {
                File.Move(quarantinePath, plan.LibraryPath);
            }

            await AddConversionHistoryAsync(
                plan,
                quarantinePath,
                sizeBytes,
                false,
                "Hardlink verification failed. Original file was restored from Quarantine.");

            return false;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(quarantinePath) &&
                File.Exists(quarantinePath) &&
                !File.Exists(plan.LibraryPath))
            {
                File.Move(quarantinePath, plan.LibraryPath);
            }

            await AddConversionHistoryAsync(
                plan,
                quarantinePath ?? string.Empty,
                sizeBytes,
                false,
                $"Conversion failed with exception: {ex.Message}");

            return false;
        }
    }

    private async Task AddConversionHistoryAsync(
        HardlinkConversionPlan plan,
        string quarantinePath,
        long sizeBytes,
        bool success,
        string message)
    {
        await _historyService.AddRecordAsync(
            new HistoryRecord
            {
                ActionType =
                    success
                        ? HistoryActionType.ConversionSucceeded
                        : HistoryActionType.ConversionFailed,

                FileName = Path.GetFileName(plan.LibraryPath),
                LibraryPath = plan.LibraryPath,
                DownloadPath = plan.DownloadPath,
                QuarantinePath = quarantinePath,
                SizeBytes = sizeBytes,
                Success = success,
                Message = message
            });
    }
}
