namespace Simplifiarr.Core.Models;

public class FileRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string FullPath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public FileType FileType { get; set; }

    public long SizeBytes { get; set; }

    public DateTime LastModifiedUtc { get; set; }

    public int? HardlinkCount { get; set; }
}

