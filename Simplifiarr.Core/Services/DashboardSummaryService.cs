using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Pure read-only rollup - does not scan anything itself and makes no
/// network calls. Every number here reflects what the last scan already
/// found and remembered.
/// </summary>
public class DashboardSummaryService : IDashboardSummaryService
{
    private readonly IQuarantineInventoryService _quarantineInventoryService;
    private readonly ISettingsService _settingsService;

    private readonly IWasteCandidateCacheService _wasteCandidateCache;
    private readonly IPromotionCandidateCacheService _promotionCandidateCache;
    private readonly IMediaPromotionService _mediaPromotion;
    private readonly IJunkFileScannerService _junkFiles;
    private readonly IBlockedFileCacheService _blockedFiles;
    private readonly IScanCoordinator _scans;
    private readonly IHistoryService _historyService;

    public DashboardSummaryService(
        IQuarantineInventoryService quarantineInventoryService,
        ISettingsService settingsService,
        IWasteCandidateCacheService wasteCandidateCache,
        IPromotionCandidateCacheService promotionCandidateCache,
        IMediaPromotionService mediaPromotion,
        IJunkFileScannerService junkFiles,
        IBlockedFileCacheService blockedFiles,
        IScanCoordinator scans,
        IHistoryService historyService)
    {
        _quarantineInventoryService = quarantineInventoryService;
        _settingsService = settingsService;

        _wasteCandidateCache = wasteCandidateCache;
        _promotionCandidateCache = promotionCandidateCache;
        _mediaPromotion = mediaPromotion;
        _junkFiles = junkFiles;
        _blockedFiles = blockedFiles;
        _scans = scans;
        _historyService = historyService;
    }

    public async Task<DashboardSummary> GetDashboardSummaryAsync()
    {
        var settings =
            await _settingsService.GetSettingsAsync();

        var enabledProfiles =
            settings.DriveProfiles
                .Where(profile => profile.Enabled)
                .ToList();

        var quarantineItems =
            await _quarantineInventoryService.GetItemsAsync();

        var quarantinedSizeBytes =
            quarantineItems.Sum(item => item.SizeBytes);

        var drives =
            enabledProfiles
                .Select(SummarizeDrive)
                .ToList();

        // The same library file can show up under two drives when their
        // folders overlap; count it once in the totals.
        var wasteCandidates =
            enabledProfiles
                .SelectMany(profile =>
                    _wasteCandidateCache.GetCandidates(profile.Name))
                .GroupBy(
                    candidate => candidate.LibraryPath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

        var possibleDuplicateCopyBytes =
            wasteCandidates.Sum(candidate => candidate.SizeBytes);

        var historyRecords =
            await _historyService.GetHistoryAsync();

        var storageSavedBytes =
            historyRecords
                .Where(record =>
                    record.ActionType ==
                    HistoryActionType.QuarantineDeleted)
                .Sum(record => record.SizeBytes);

        return new DashboardSummary
        {
            HealthyHardlinkCount =
                drives.Sum(drive => drive.HealthyHardlinkCount),

            PossibleDuplicateCopyCount =
                wasteCandidates.Count,

            PossibleDuplicateCopyBytes =
                possibleDuplicateCopyBytes,

            PossibleDuplicateCopySizeFormatted =
                SizeFormatter.FormatBytes(possibleDuplicateCopyBytes),

            QuarantinedFileCount =
                quarantineItems.Count,

            QuarantinedSizeBytes =
                quarantinedSizeBytes,

            QuarantinedSizeFormatted =
                SizeFormatter.FormatBytes(quarantinedSizeBytes),

            PromotionOpportunityCount =
                drives.Sum(drive => drive.ToLinkCount),

            JunkFileCount =
                drives.Sum(drive => drive.LeftoverFileCount),

            BlockedFileCount =
                drives.Sum(drive => drive.BlockedFileCount),

            StorageSavedBytes =
                storageSavedBytes,

            StorageSavedFormatted =
                SizeFormatter.FormatBytes(storageSavedBytes),

            EnabledDriveProfileCount =
                enabledProfiles.Count,

            Drives =
                drives
        };
    }

    private DriveSummary SummarizeDrive(DriveProfile profile)
    {
        var fullCopies =
            _wasteCandidateCache.GetCandidates(profile.Name);

        var toLink =
            _promotionCandidateCache.GetCandidates(profile.Name).Count +
            MediaTypeDefinitions.All.Sum(definition =>
                _mediaPromotion.GetCandidates(definition.MediaType, profile.Name).Count);

        return new DriveSummary
        {
            Name = profile.Name,
            ToLinkCount = toLink,
            FullCopyCount = fullCopies.Count,
            FullCopyBytes = fullCopies.Sum(candidate => candidate.SizeBytes),
            LeftoverFileCount = _junkFiles.GetCandidates(profile.Name).Count,
            BlockedFileCount = _blockedFiles.GetBlockedFiles(profile.Name).Count,
            HealthyHardlinkCount = _wasteCandidateCache.GetHealthyHardlinkCount(profile.Name),
            LastScanUtc = _scans.GetLastScanUtc(profile.Name),
            SetupProblems = DriveProfileValidator.GetProblems(profile)
        };
    }
}
