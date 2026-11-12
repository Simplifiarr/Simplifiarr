using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Pure read-only rollup - does not scan anything itself and makes no
/// network calls. Every number here reflects what the last scan already
/// found and remembered (duplicates are worked out from the scan's catalog).
/// </summary>
public class DashboardSummaryService : IDashboardSummaryService
{
    private readonly IQuarantineInventoryService _quarantineInventoryService;
    private readonly ISettingsService _settingsService;

    private readonly IWasteCandidateCacheService _wasteCandidateCache;
    private readonly IPromotionCandidateCacheService _promotionCandidateCache;
    private readonly IMediaPromotionService _mediaPromotion;
    private readonly IJunkFileScannerService _junkFiles;
    private readonly IPlaybackErrorService _playbackErrors;
    private readonly IScanCoordinator _scans;
    private readonly IHistoryService _historyService;
    private readonly IMediaCatalogService _catalog;
    private readonly IDuplicateAssetAnalysisService _duplicates;
    private readonly IIgnoredItemsService _ignored;

    public DashboardSummaryService(
        IQuarantineInventoryService quarantineInventoryService,
        ISettingsService settingsService,
        IWasteCandidateCacheService wasteCandidateCache,
        IPromotionCandidateCacheService promotionCandidateCache,
        IMediaPromotionService mediaPromotion,
        IJunkFileScannerService junkFiles,
        IPlaybackErrorService playbackErrors,
        IScanCoordinator scans,
        IHistoryService historyService,
        IMediaCatalogService catalog,
        IDuplicateAssetAnalysisService duplicates,
        IIgnoredItemsService ignored)
    {
        _quarantineInventoryService = quarantineInventoryService;
        _settingsService = settingsService;

        _wasteCandidateCache = wasteCandidateCache;
        _promotionCandidateCache = promotionCandidateCache;
        _mediaPromotion = mediaPromotion;
        _junkFiles = junkFiles;
        _playbackErrors = playbackErrors;
        _scans = scans;
        _historyService = historyService;
        _catalog = catalog;
        _duplicates = duplicates;
        _ignored = ignored;
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

        // Duplicates aren't saved by the scan; they are worked out from the
        // catalog the same way the Duplicates page does it.
        var duplicateGroups =
            DuplicateAssetAnalysisService.RemoveIgnored(
                await _duplicates.FindDuplicatesAsync(
                    _catalog.GetAssets(),
                    enabledProfiles),
                _ignored);

        var duplicateReclaimableBytes =
            duplicateGroups.Sum(group => group.ReclaimableSizeBytes);

        var drives =
            enabledProfiles
                .Select(profile => SummarizeDrive(profile, duplicateGroups))
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

            PlaybackErrorCount =
                drives.Sum(drive => drive.PlaybackErrorCount),

            DuplicateTitleCount =
                duplicateGroups.Count,

            DuplicateReclaimableBytes =
                duplicateReclaimableBytes,

            DuplicateReclaimableFormatted =
                SizeFormatter.FormatBytes(duplicateReclaimableBytes),

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

    private DriveSummary SummarizeDrive(
        DriveProfile profile,
        IReadOnlyList<DuplicateAssetGroup> duplicateGroups)
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
            PlaybackErrorCount = _playbackErrors.GetForDrive(profile.Name).Count,
            DuplicateTitleCount = duplicateGroups.Count(group =>
                group.Entries.Any(entry =>
                    string.Equals(entry.DriveProfileName, profile.Name, StringComparison.OrdinalIgnoreCase))),
            HealthyHardlinkCount = _wasteCandidateCache.GetHealthyHardlinkCount(profile.Name),
            LastScanUtc = _scans.GetLastScanUtc(profile.Name),
            SetupProblems = DriveProfileValidator.GetProblems(profile)
        };
    }
}
