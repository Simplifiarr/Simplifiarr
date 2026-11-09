namespace Simplifiarr.Core.Models;

public class HistoryRecord
{
    public Guid Id { get; set; } =
        Guid.NewGuid();

    public DateTime TimestampUtc { get; set; } =
        DateTime.UtcNow;

    public HistoryActionType ActionType { get; set; }

    public string FileName { get; set; } =
        string.Empty;

    public string LibraryPath { get; set; } =
        string.Empty;

    public string DownloadPath { get; set; } =
        string.Empty;

    public string QuarantinePath { get; set; } =
        string.Empty;

    public long SizeBytes { get; set; }

    public bool Success { get; set; }

    public string Message { get; set; } =
        string.Empty;
}
