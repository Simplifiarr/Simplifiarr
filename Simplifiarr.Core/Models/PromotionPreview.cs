namespace Simplifiarr.Core.Models;

public class PromotionPreview
{
    public string SourcePath { get; set; } =
        string.Empty;

    public string TargetDirectory { get; set; } =
        string.Empty;

    public string TargetFilePath { get; set; } =
        string.Empty;

    public bool CanPromote { get; set; }

    public string Reason { get; set; } =
        string.Empty;

    public List<string> PlannedActions { get; set; } =
        [];
}