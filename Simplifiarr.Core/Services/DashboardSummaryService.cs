using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Pure read-only rollup - does not scan anything itself and makes no
/// network calls. Every number here reflects whatever the last relevant
/// scan (on Hardlinks / Potential Waste / Books / Audiobooks / Music /
/// Comics) already found and cached.
/// </summary>
public class DashboardSummaryService : IDashboardSummaryService
{
    private readonly IQuarantineInventoryService _quarantineInventoryService;
    private readonly ISettingsService _settingsService;

    private readonly IWasteCandidateCacheService _wasteCandidateCache;
    private readonly IPromotionCandidateCacheService _promotionCandidateCache;
    private readonly IMediaPromotionService _mediaPromotion;
    private readonly IHistoryService _historyService;

    public DashboardSummaryService(
        IQuarantineInventoryService quarantineInventoryService,
        ISettingsService settingsService,
        IWasteCandidateCacheService wasteCandidateCache,
        IPromotionCandidateCacheService promotionCandidateCache,
        IMediaPromotionService mediaPromotion,
        IHistoryService historyService)
    {
        _quarantineInventoryService = quarantineInventoryService;
        _settingsService = settingsService;

        _wasteCandidateCache = wasteCandidateCache;
        _promotionCandidateCache = promotionCandidateCache;
        _mediaPromotion = mediaPromotion;
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

        var healthyHardlinkCount =
            enabledProfiles.Sum(profile =>
                _wasteCandidateCache.GetHealthyHardlinkCount(profile.Name));

        var promotionOpportunityCount =
            enabledProfiles.Sum(profile =>
                _promotionCandidateCache.GetCandidates(profile.Name).Count) +
            MediaTypeDefinitions.All.Sum(definition =>
                enabledProfiles.Sum(profile =>
                    _mediaPromotion.GetCandidates(definition.MediaType, profile.Name).Count));

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
                healthyHardlinkCount,

            PossibleDuplicateCopyCount =
                wasteCandidates.Count,

            PossibleDuplicateCopySizeFormatted =
                SizeFormatter.FormatBytes(possibleDuplicateCopyBytes),

            QuarantinedFileCount =
                quarantineItems.Count,

            QuarantinedSizeBytes =
                quarantinedSizeBytes,

            QuarantinedSizeFormatted =
                SizeFormatter.FormatBytes(quarantinedSizeBytes),

            PromotionOpportunityCount =
                promotionOpportunityCount,

            StorageSavedBytes =
                storageSavedBytes,

            StorageSavedFormatted =
                SizeFormatter.FormatBytes(storageSavedBytes),

            EnabledDriveProfileCount =
                enabledProfiles.Count
        };
    }
}
