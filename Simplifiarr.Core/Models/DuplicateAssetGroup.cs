namespace Simplifiarr.Core.Models;

public class DuplicateAssetGroup
{
    public MediaType MediaType { get; set; }

    public string Title { get; set; } =
        string.Empty;

    public int? Year { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string? Author { get; set; }

    public List<DuplicateAssetEntry> Entries { get; set; } =
        [];

    public long TotalSizeBytes =>
        Entries.Sum(entry => entry.SizeBytes);

    public long ReclaimableSizeBytes =>
        Entries.Count > 1
            ? TotalSizeBytes - Entries.Max(entry => entry.SizeBytes)
            : 0;
}

public class DuplicateAssetEntry
{
    public string CanonicalPath { get; set; } =
        string.Empty;

    public long SizeBytes { get; set; }

    public string Resolution { get; set; } =
        string.Empty;

    public string VideoCodec { get; set; } =
        string.Empty;

    public string DriveProfileName { get; set; } =
        string.Empty;
}
