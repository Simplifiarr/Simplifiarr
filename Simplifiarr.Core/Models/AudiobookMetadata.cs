namespace Simplifiarr.Core.Models;

public class AudiobookMetadata
{
    public string Title { get; set; } =
        string.Empty;

    public string? Author { get; set; }

    public string? Narrator { get; set; }

    public AudiobookMetadataSource Source { get; set; }
}
