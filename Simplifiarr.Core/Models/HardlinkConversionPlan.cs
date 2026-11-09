namespace Simplifiarr.Core.Models;

public class HardlinkConversionPlan
{
    public string LibraryPath { get; set; } = string.Empty;

    public string DownloadPath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public string SizeFormatted { get; set; } = string.Empty;

    public bool LibraryFileExists { get; set; }

    public bool DownloadFileExists { get; set; }

    public string LibraryRoot { get; set; } = string.Empty;

    public string DownloadRoot { get; set; } = string.Empty;

    public bool SameVolume { get; set; }

    public bool CanConvert { get; set; }

    public string Reason { get; set; } = string.Empty;
}

