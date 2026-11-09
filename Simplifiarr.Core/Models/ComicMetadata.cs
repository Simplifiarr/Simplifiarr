namespace Simplifiarr.Core.Models;

public class ComicMetadata
{
    public string Title { get; set; } =
        string.Empty;

    public string? Series { get; set; }

    public double? Issue { get; set; }

    public string? Author { get; set; }

    public int? Year { get; set; }

    public ComicMetadataSource Source { get; set; }
}
