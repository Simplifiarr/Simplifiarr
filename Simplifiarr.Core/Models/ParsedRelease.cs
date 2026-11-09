namespace Simplifiarr.Core.Models;

public class ParsedRelease
{
    public string OriginalName { get; set; } =
        string.Empty;

    public string NormalizedTitle { get; set; } =
        string.Empty;

    public int? Year { get; set; }

    public bool IsTv { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string Resolution { get; set; } =
        string.Empty;

    public string Codec { get; set; } =
        string.Empty;
}

