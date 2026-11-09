namespace Simplifiarr.Core.Models;

public class MusicMetadata
{
    public string Title { get; set; } =
        string.Empty;

    public string? Artist { get; set; }

    public string? Album { get; set; }

    public double? TrackNumber { get; set; }

    public MusicMetadataSource Source { get; set; }
}
