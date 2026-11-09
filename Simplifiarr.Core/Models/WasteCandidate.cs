namespace Simplifiarr.Core.Models;

public class WasteCandidate
{
    public string LibraryPath { get; set; } =
        string.Empty;

    public string DownloadPath { get; set; } =
        string.Empty;

    public string LibraryFileName { get; set; } =
        string.Empty;

    public string DownloadFileName { get; set; } =
        string.Empty;

    public long SizeBytes { get; set; }

    public MediaKind Kind { get; set; }

    public string DriveProfileName { get; set; } =
        string.Empty;

    public DateTime DiscoveredUtc { get; set; }

    public HardlinkConversionPlan? Plan { get; set; }

    public bool CanConvert { get; set; }

    public string Reason { get; set; } =
        string.Empty;
}
