namespace Simplifiarr.Core.Models;

public class AssetIdentityResult
{
    public bool IsRepresented { get; set; }

    public int MatchingFileCount { get; set; }

    public string Reason { get; set; } =
        string.Empty;

    public List<string> MatchingFiles { get; set; } =
        [];
}