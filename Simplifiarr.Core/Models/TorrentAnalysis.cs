namespace Simplifiarr.Core.Models;

public class TorrentAnalysis
{
    public Guid TorrentId { get; set; }

    public string TorrentName { get; set; } =
        string.Empty;

    public TorrentClassification Classification { get; set; }

    public string Reason { get; set; } =
        string.Empty;

    public string SavePath { get; set; } =
        string.Empty;
}

