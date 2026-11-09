namespace Simplifiarr.Core.Models;

public class QuarantineItem
{
    public string FileName { get; set; } = string.Empty;

    public string FullPath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime CreatedUtc { get; set; }
}

