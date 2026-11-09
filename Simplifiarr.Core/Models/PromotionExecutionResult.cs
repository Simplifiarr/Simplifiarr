namespace Simplifiarr.Core.Models;

public class PromotionExecutionResult
{
    public bool Success { get; set; }

    public string Reason { get; set; } =
        string.Empty;

    public bool DirectoryCreated { get; set; }

    public bool HardlinkCreated { get; set; }

    public bool VerificationPassed { get; set; }
}