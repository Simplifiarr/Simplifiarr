namespace Simplifiarr.Core.Models;

public class WholeTorrentReconstructionResult
{
    public bool Success { get; set; }

    public List<string> HardlinkedPaths { get; set; } =
        [];

    public string? TorrentFilePath { get; set; }

    /// <summary>
    /// The folder every file was hardlinked under (and the .torrent saved
    /// to) - also what a download client's save path should point at.
    /// </summary>
    public string? OutputRoot { get; set; }

    public string? FailureReason { get; set; }
}
