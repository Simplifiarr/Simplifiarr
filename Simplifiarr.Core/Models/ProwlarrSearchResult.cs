namespace Simplifiarr.Core.Models;

public class ProwlarrSearchResult
{
    public string Title { get; set; } =
        string.Empty;

    public string Indexer { get; set; } =
        string.Empty;

    public long SizeBytes { get; set; }

    public int Seeders { get; set; }

    public int Leechers { get; set; }

    public DateTime? PublishDate { get; set; }

    public string DownloadUrl { get; set; } =
        string.Empty;

    public string InfoUrl { get; set; } =
        string.Empty;

    public string Protocol { get; set; } =
        string.Empty;
}
