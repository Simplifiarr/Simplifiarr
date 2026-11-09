namespace Simplifiarr.Core.Models;

/// <summary>
/// A cataloged asset whose file carries a Windows "Mark of the Web"
/// (a Zone.Identifier alternate data stream), meaning Windows still thinks
/// it came from the internet/another computer - report-only, flags it for
/// the user rather than unblocking anything automatically.
/// </summary>
public class BlockedFile
{
    public Guid MediaAssetId { get; set; }

    public string Title { get; set; } =
        string.Empty;

    public MediaType MediaType { get; set; }

    public string CanonicalPath { get; set; } =
        string.Empty;

    public long SizeBytes { get; set; }

    public string DriveProfileName { get; set; } =
        string.Empty;

    /// <summary>
    /// The HostUrl= value from the Zone.Identifier stream's own content,
    /// when present - shows where Windows thinks the file came from.
    /// </summary>
    public string? SourceUrl { get; set; }

    public DateTime DiscoveredUtc { get; set; }
}
