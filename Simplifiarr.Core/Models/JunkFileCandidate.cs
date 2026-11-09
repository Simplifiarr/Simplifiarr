namespace Simplifiarr.Core.Models;

public class JunkFileCandidate
{
    public string FilePath { get; set; } =
        string.Empty;

    public string FileName { get; set; } =
        string.Empty;

    public long SizeBytes { get; set; }

    public string Reason { get; set; } =
        string.Empty;

    public string DriveProfileName { get; set; } =
        string.Empty;
}
