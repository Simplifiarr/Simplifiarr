namespace Simplifiarr.Core.Models;

public class DashboardSummary
{
    public int PossibleDuplicateCopyCount { get; set; }

    public string PossibleDuplicateCopySizeFormatted { get; set; } = string.Empty;

    public int HealthyHardlinkCount { get; set; }

    public int QuarantinedFileCount { get; set; }

    public long QuarantinedSizeBytes { get; set; }

    public string QuarantinedSizeFormatted { get; set; } = string.Empty;

    public int PromotionOpportunityCount { get; set; }

    public long StorageSavedBytes { get; set; }

    public string StorageSavedFormatted { get; set; } = string.Empty;

    public int EnabledDriveProfileCount { get; set; }
}
