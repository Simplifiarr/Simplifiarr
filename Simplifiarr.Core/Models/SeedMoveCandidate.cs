namespace Simplifiarr.Core.Models;

/// <summary>
/// A torrent seeding from one drive while its files were copied into a
/// library on another (Sonarr and Radarr copy when they can't hardlink
/// across drives). Moving the seed to the library's drive leaves one copy.
/// </summary>
public class SeedMoveCandidate
{
    public string TorrentHash { get; set; } = string.Empty;

    public string TorrentName { get; set; } = string.Empty;

    /// <summary>The tracker's host name, for telling torrents apart.</summary>
    public string TrackerHost { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    /// <summary>Where qBittorrent keeps the torrent now.</summary>
    public string SavePath { get; set; } = string.Empty;

    /// <summary>The matching downloads folder on the library's drive.</summary>
    public string TargetSavePath { get; set; } = string.Empty;

    public string SourceDriveName { get; set; } = string.Empty;

    public string TargetDriveName { get; set; } = string.Empty;

    public List<SeedMoveFile> Files { get; set; } = [];

    public DateTime DiscoveredUtc { get; set; } = DateTime.UtcNow;

    public long TotalBytes =>
        Files.Sum(file => file.SizeBytes);

    /// <summary>Space freed: files that will share the library's copy.</summary>
    public long LinkedBytes =>
        Files.Where(file => file.InLibrary).Sum(file => file.SizeBytes);

    /// <summary>Files with no library copy, which qBittorrent copies across.</summary>
    public long CopiedBytes =>
        TotalBytes - LinkedBytes;
}

public class SeedMoveFile
{
    /// <summary>Path relative to the torrent's save folder, as qBittorrent lists it.</summary>
    public string RelativePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>
    /// Library files on the target drive of the same size. Which one is the
    /// same file is settled when moving; qBittorrent's recheck then proves it.
    /// </summary>
    public List<string> LibraryCandidates { get; set; } = [];

    public bool InLibrary =>
        LibraryCandidates.Count > 0;
}

/// <summary>What happened to one Move Seeds request.</summary>
public sealed record SeedMoveResult(
    bool Succeeded,
    string Message,
    long FreedBytes = 0);

/// <summary>The move in progress, for showing on the page.</summary>
public sealed record SeedMoveProgress(
    string TorrentHash,
    string TorrentName,
    string Step);
