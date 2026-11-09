namespace Simplifiarr.Core.Models;

public class MediaMetadata
{
    public int Width { get; set; }

    public int Height { get; set; }

    public string Codec { get; set; } =
        string.Empty;

    public string Resolution { get; set; } =
        string.Empty;
}
