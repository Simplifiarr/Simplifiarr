namespace Simplifiarr.Core.Models;

public class DownloadCleanupRemoveResult
{
    /// <summary>Set when nothing could be removed safely (for example the client was unreachable).</summary>
    public string? Error { get; set; }

    public int Removed { get; set; }

    /// <summary>Candidates left untouched because a safety re-check failed, or the move failed.</summary>
    public int Skipped { get; set; }

    /// <summary>Why individual candidates were skipped.</summary>
    public List<string> SkipReasons { get; set; } =
        [];
}
