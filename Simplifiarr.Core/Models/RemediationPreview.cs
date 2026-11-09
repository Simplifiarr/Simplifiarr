namespace Simplifiarr.Core.Models;

public class RemediationPreview
{
    public string FileName { get; set; } = string.Empty;

    public string LibraryPath { get; set; } = string.Empty;

    public string DownloadPath { get; set; } = string.Empty;

    public string ExpectedSavings { get; set; } = string.Empty;

    public bool CanConvert { get; set; }

    public List<string> SafetyChecksPassed { get; set; } = new();

    public List<string> PlannedActions { get; set; } = new();

    public string Reason { get; set; } = string.Empty;
}

