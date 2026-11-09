namespace Simplifiarr.Core.Models;

public class IgnoredItem
{
    public string SourcePath { get; set; } =
        string.Empty;

    public string Title { get; set; } =
        string.Empty;

    public IgnoredCategory Category { get; set; }

    public string? Author { get; set; }

    public int? Year { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string DriveProfileName { get; set; } =
        string.Empty;

    public DateTime IgnoredUtc { get; set; }

    public string Reason { get; set; } =
        string.Empty;
}
