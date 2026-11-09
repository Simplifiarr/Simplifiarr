namespace Simplifiarr.Core.Models;

public class LibraryTarget
{
    public string TargetDirectory { get; set; } =
        string.Empty;

    public string TargetFilePath { get; set; } =
        string.Empty;

    public bool DirectoryExists { get; set; }

    public bool FileExists { get; set; }

    public bool IsTv { get; set; }
}