namespace Simplifiarr.Core.Models;

public class DuplicateCandidate
{
    public string LibraryPath { get; set; } = string.Empty;

    public string DownloadPath { get; set; } = string.Empty;

    public string LibraryFileName { get; set; } = string.Empty;

    public string DownloadFileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int? LibraryHardlinkCount { get; set; }

    public bool IsAlreadyHardlinked => LibraryHardlinkCount.HasValue && LibraryHardlinkCount.Value > 1;
}

