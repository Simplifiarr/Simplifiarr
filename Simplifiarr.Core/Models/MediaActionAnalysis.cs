namespace Simplifiarr.Core.Models;

public class MediaActionAnalysis
{
    public string Title { get; set; } =
        string.Empty;

    public int? Year { get; set; }

    public MediaActionType ActionType { get; set; }

    public string Reason { get; set; } =
        string.Empty;
}