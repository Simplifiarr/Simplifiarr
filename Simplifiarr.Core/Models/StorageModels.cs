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

    public bool IsSeeding => SeedingBytes > 0;

    /// <summary>Only movies and TV: Radarr and Sonarr are told the new place.</summary>
    public bool CanMove =>
        !IsSeeding &&
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

    /// <summary>Above this used share the drive counts as getting full.</summary>
    public int LimitPercent { get; set; }

    public long LimitBytes => (long)(TotalBytes * (LimitPercent / 100.0));

    /// <summary>How much has to go to get back under the limit; 0 when under.</summary>
    public long OverLimitBytes => Math.Max(0, UsedBytes - LimitBytes);

    public bool IsOverLimit => OverLimitBytes > 0;

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
