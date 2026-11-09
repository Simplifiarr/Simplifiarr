namespace Simplifiarr.Core.Models;

public class SourceRecord
{
    public Guid Id { get; set; } =
        Guid.NewGuid();

    public Guid MediaAssetId { get; set; }

    public SourceType SourceType { get; set; } =
        SourceType.Unknown;

    public string ExternalId { get; set; } =
        string.Empty;

    public DateTime DiscoveredUtc { get; set; } =
        DateTime.UtcNow;
}
