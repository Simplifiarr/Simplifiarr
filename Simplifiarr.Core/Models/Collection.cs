namespace Simplifiarr.Core.Models;

public class Collection
{
    public Guid Id { get; set; } =
        Guid.NewGuid();

    public string Name { get; set; } =
        string.Empty;

    public string DriveProfileName { get; set; } =
        string.Empty;

    public List<Guid> AssetIds { get; set; } =
        [];

    public DateTime CreatedUtc { get; set; } =
        DateTime.UtcNow;

    public DateTime? LastBuiltUtc { get; set; }
}
