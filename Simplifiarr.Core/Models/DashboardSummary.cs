namespace Simplifiarr.Core.Models;

public class DashboardSummary
{
    public int PossibleDuplicateCopyCount { get; set; }

    public long PossibleDuplicateCopyBytes { get; set; }

    public string PossibleDuplicateCopySizeFormatted { get; set; } = string.Empty;

    public int HealthyHardlinkCount { get; set; }

    public int QuarantinedFileCount { get; set; }

    public long QuarantinedSizeBytes { get; set; }

    public string QuarantinedSizeFormatted { get; set; } = string.Empty;

    public int PromotionOpportunityCount { get; set; }

    public int JunkFileCount { get; set; }

    public int PlaybackErrorCount { get; set; }

    /// <summary>Titles that are in your libraries more than once.</summary>
    public int DuplicateTitleCount { get; set; }

    /// <summary>Space freed by keeping only the largest copy of each.</summary>
    public long DuplicateReclaimableBytes { get; set; }

    public string DuplicateReclaimableFormatted { get; set; } = string.Empty;

    public long StorageSavedBytes { get; set; }

    public string StorageSavedFormatted { get; set; } = string.Empty;

    public int EnabledDriveProfileCount { get; set; }

    /// <summary>
    /// One row per enabled drive, in the order they are set up.
    /// </summary>
    public List<DriveSummary> Drives { get; set; } = [];
}

/// <summary>
/// What the last scan found on one drive.
/// </summary>
public class DriveSummary
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Downloads that could be linked into a library.</summary>
    public int ToLinkCount { get; set; }

    /// <summary>Library files that are full copies of a download.</summary>
    public int FullCopyCount { get; set; }

    public long FullCopyBytes { get; set; }

    public int LeftoverFileCount { get; set; }

    public int PlaybackErrorCount { get; set; }

    /// <summary>Duplicate titles with at least one copy on this drive.</summary>
    public int DuplicateTitleCount { get; set; }

    public int HealthyHardlinkCount { get; set; }

    public DateTime? LastScanUtc { get; set; }

    /// <summary>
    /// Folder-setup problems that make this drive's results unreliable.
    /// </summary>
    public List<string> SetupProblems { get; set; } = [];

    public int ItemsToReview =>
        ToLinkCount + FullCopyCount + LeftoverFileCount + PlaybackErrorCount + DuplicateTitleCount;
}
