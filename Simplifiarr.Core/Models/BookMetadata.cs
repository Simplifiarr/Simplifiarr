namespace Simplifiarr.Core.Models;

public class BookMetadata
{
    public string Title { get; set; } =
        string.Empty;

    public string? Author { get; set; }

    public BookMetadataSource Source { get; set; }
}
