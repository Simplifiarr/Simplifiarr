namespace Simplifiarr.Core.Models;

/// <summary>
/// One title's folder in a library: a movie's folder, a show's folder, a
/// book's folder. What Storage measures, suggests and moves.
/// </summary>
public sealed class StorageTitle
{
    public string DriveName { get; set; } = string.Empty;

    /// <summary>"Movies", "TV", "Books" and so on.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string FolderPath { get; set; } = string.Empty;

    public string LibraryPath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int FileCount { get; set; }

    /// <summary>
    /// Bytes also linked into a downloads folder, i.e. still seeding.
    /// Moving the library copy alone would free none of it.
    /// </summary>
    public long SeedingBytes { get; set; }

    /// <summary>When the newest file in it was written.</summary>
    public DateTime LastWriteUtc { get; set; }

    /// <summary>
    /// When the newest video in it was written. New artwork, .nfo files
    /// and subtitles don't make an old movie new. Unset in measurements
    /// made before this was kept, or when there is no video.
    /// </summary>
    public DateTime LastVideoWriteUtc { get; set; }

    /// <summary>When its content last changed: the newest video, or any file when there's none.</summary>
    public DateTime ContentChangedUtc =>
        LastVideoWriteUtc > DateTime.MinValue ? LastVideoWriteUtc : LastWriteUtc;

    public bool IsSeeding => SeedingBytes > 0;

    /// <summary>
    /// A show Sonarr still expects episodes of, on the downloads drive. It
    /// stays there, so new episodes keep being hardlinked rather than copied
    /// to another drive.
    /// </summary>
    public bool StaysForNewEpisodes { get; set; }

    /// <summary>Only movies and TV: Radarr and Sonarr are told the new place.</summary>
    public bool CanMove =>
        !IsSeeding &&
        !StaysForNewEpisodes &&
        Kind is StorageKinds.Movies or StorageKinds.Tv;
}

public static class StorageKinds
{
    public const string Movies = "Movies";
    public const string Tv = "TV";
    public const string Books = "Books";
    public const string Audiobooks = "Audiobooks";
    public const string Music = "Music";
    public const string Comics = "Comics";
}

/// <summary>A physical drive: its space, its limit, and its titles.</summary>
public sealed class StorageDrive
{
    /// <summary>"C:\"</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>The drive entries in Settings on this drive.</summary>
    public List<string> ProfileNames { get; set; } = [];

    public long TotalBytes { get; set; }

    public long FreeBytes { get; set; }

    public long UsedBytes => TotalBytes - FreeBytes;

    /// <summary>Used share of the drive, 0 to 100.</summary>
    public double UsedPercent =>
        TotalBytes <= 0 ? 0 : UsedBytes * 100.0 / TotalBytes;

    /// <summary>
    /// Protected drives are kept clear and never take moves; the others
    /// are overflow drives.
    /// </summary>
    public bool IsProtected { get; set; }

    /// <summary>The drive new downloads land on (it is also protected).</summary>
    public bool IsDownloadsDrive { get; set; }

    /// <summary>A protected drive starts moving titles off above this used share.</summary>
    public int LimitPercent { get; set; }

    /// <summary>A protected drive is brought back down to this used share.</summary>
    public int BringBackToPercent { get; set; }

    /// <summary>What an overflow drive was set to keep free; null for a tenth of it.</summary>
    public long? KeepFreeSetBytes { get; set; }

    /// <summary>What an overflow drive always keeps free.</summary>
    public long KeepFreeBytes => KeepFreeSetBytes ?? TotalBytes / 10;

    /// <summary>Used space above which titles start moving off.</summary>
    public long LimitBytes =>
        IsProtected
            ? (long)(TotalBytes * (LimitPercent / 100.0))
            : Math.Max(0, TotalBytes - KeepFreeBytes);

    /// <summary>Used space a drive is brought back to once it is over its limit.</summary>
    public long TargetBytes =>
        IsProtected
            ? Math.Min(LimitBytes, (long)(TotalBytes * (BringBackToPercent / 100.0)))
            : LimitBytes;

    /// <summary>Where the limit sits, as a used share, for the bar.</summary>
    public double LimitLinePercent =>
        TotalBytes <= 0 ? 0 : LimitBytes * 100.0 / TotalBytes;

    /// <summary>How far over its limit it is; 0 when under.</summary>
    public long OverLimitBytes => Math.Max(0, UsedBytes - LimitBytes);

    public bool IsOverLimit => OverLimitBytes > 0;

    /// <summary>How much has to move to bring it back to its target; 0 when under its limit.</summary>
    public long ToFreeBytes => IsOverLimit ? Math.Max(0, UsedBytes - TargetBytes) : 0;

    public bool IsAvailable => TotalBytes > 0;

    public List<StorageTitle> Titles { get; set; } = [];
}

/// <summary>Move this title to that drive's library.</summary>
public sealed record StorageMoveSuggestion(
    StorageTitle Title,
    string FromRoot,
    string ToRoot,
    string ToDriveName,
    string ToLibraryPath);
