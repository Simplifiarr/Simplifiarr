using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Pure read-only rollup - does not scan anything itself and makes no
/// network calls. Every number here reflects what the last scan already
/// found and remembered (Copies' last check for copies and versions).
/// </summary>
public class DashboardSummaryService : IDashboardSummaryService
{
    private readonly IQuarantineInventoryService _quarantineInventoryService;
    private readonly ISettingsService _settingsService;

    private readonly ICopiesService _copies;
    private readonly IDownloadsDriveCheck? _downloadsDrive;
    private readonly IPromotionCandidateCacheService _promotionCandidateCache;
    private readonly IMediaPromotionService _mediaPromotion;
    private readonly IJunkFileScannerService _junkFiles;
    private readonly IPlaybackErrorService _playbackErrors;
    private readonly IScanCoordinator _scans;
    private readonly IHistoryService _historyService;
    private readonly ISeedMoveService _seedMoves;

    public DashboardSummaryService(
        IQuarantineInventoryService quarantineInventoryService,
        ISettingsService settingsService,
        ICopiesService copies,
        IPromotionCandidateCacheService promotionCandidateCache,
        IMediaPromotionService mediaPromotion,
        IJunkFileScannerService junkFiles,
        IPlaybackErrorService playbackErrors,
        IScanCoordinator scans,
        IHistoryService historyService,
        ISeedMoveService seedMoves,
        IDownloadsDriveCheck? downloadsDrive = null)
    {
        _downloadsDrive = downloadsDrive;
        _quarantineInventoryService = quarantineInventoryService;
        _settingsService = settingsService;

        _copies = copies;
        _promotionCandidateCache = promotionCandidateCache;
        _mediaPromotion = mediaPromotion;
        _junkFiles = junkFiles;
        _playbackErrors = playbackErrors;
        _scans = scans;
        _historyService = historyService;
        _seedMoves = seedMoves;
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

        var copies =
            _copies.Last ?? new CopiesScanResult();

        var matched =
            new HashSet<string>(copies.MatchedDownloads, StringComparer.OrdinalIgnoreCase);

        var duplicateReclaimableBytes =
            copies.VersionBytes;

        var seedMoves =
            enabledProfiles
                .SelectMany(profile => _seedMoves.GetCandidates(profile.Name))
                .ToList();

        var seedMoveBytes =
            seedMoves.Sum(candidate => candidate.LinkedBytes);

        var drives =
            enabledProfiles
                .Select(profile => SummarizeDrive(profile, copies, matched))
                .ToList();

        var possibleDuplicateCopyBytes =
            copies.ExtraCopyBytes;

        // Two drive entries can share a downloads folder: each download once.
        var toLink =
            enabledProfiles
                .SelectMany(profile =>
                    _promotionCandidateCache.GetCandidates(profile.Name).Select(candidate => candidate.SourcePath)
                        .Concat(MediaTypeDefinitions.All.SelectMany(definition =>
                            _mediaPromotion.GetCandidates(definition.MediaType, profile.Name).Select(candidate => candidate.SourcePath))))
                .Where(path => !matched.Contains(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

        var historyRecords =
            await _historyService.GetHistoryAsync();

        var savings =
            SpaceSavings.Tally(historyRecords);

        var storageSavedBytes =
            savings.Sum(saving => saving.Bytes);

        return new DashboardSummary
        {
            HealthyHardlinkCount =
                copies.AlreadyLinkedCount,

            PossibleDuplicateCopyCount =
                copies.ExtraCopyCount,

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
                toLink,

            JunkFileCount =
                drives.Sum(drive => drive.LeftoverFileCount),

            PlaybackErrorCount =
                drives.Sum(drive => drive.PlaybackErrorCount),

            DuplicateTitleCount =
                copies.Versions.Count,

            SeedMoveCount =
                seedMoves.Count,

            SeedMoveBytes =
                seedMoveBytes,

            SeedMoveFormatted =
                SizeFormatter.FormatBytes(seedMoveBytes),

            DuplicateReclaimableBytes =
                duplicateReclaimableBytes,

            DuplicateReclaimableFormatted =
                SizeFormatter.FormatBytes(duplicateReclaimableBytes),

            Savings =
                savings,

            DownloadsDriveProblems =
                _downloadsDrive?.Problems ?? [],

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
        CopiesScanResult copies,
        HashSet<string> matched)
    {
        bool Here(string drive) =>
            string.Equals(drive, profile.Name, StringComparison.OrdinalIgnoreCase);

        var toLink =
            _promotionCandidateCache.GetCandidates(profile.Name).Count(candidate => !matched.Contains(candidate.SourcePath)) +
            MediaTypeDefinitions.All.Sum(definition =>
                _mediaPromotion.GetCandidates(definition.MediaType, profile.Name).Count(candidate => !matched.Contains(candidate.SourcePath)));

        var deletes = copies.ToDelete.Where(leftover => Here(leftover.DriveProfileName)).ToList();
        var links = copies.ToLink.Where(link => Here(link.Library.DriveName)).ToList();

        return new DriveSummary
        {
            Name = profile.Name,
            ToLinkCount = toLink,
            FullCopyCount = deletes.Count + links.Count,
            FullCopyBytes = deletes.Sum(leftover => leftover.FreedBytes) + links.Sum(link => link.FreedBytes),
            LeftoverFileCount = _junkFiles.GetCandidates(profile.Name).Count,
            PlaybackErrorCount = _playbackErrors.GetForDrive(profile.Name).Count,
            DuplicateTitleCount = copies.Versions.Count(group => group.Versions.Any(version => Here(version.DriveName))),
            SeedMoveCount = _seedMoves.GetCandidates(profile.Name).Count,
            HealthyHardlinkCount = copies.LinkedByDrive.GetValueOrDefault(profile.Name),
            LastScanUtc = _scans.GetLastScanUtc(profile.Name),
            SetupProblems = DriveProfileValidator.GetProblems(profile)
        };
    }
}
