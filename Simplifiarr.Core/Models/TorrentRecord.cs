namespace Simplifiarr.Core.Models;

public class TorrentRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string TorrentHash { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string SavePath { get; set; } = string.Empty;

    public string ContentPath { get; set; } = string.Empty;

    /// <summary>The client's own word for the state, for showing and logs.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The state in Simplifiarr's words, the same for every client.</summary>
    public TorrentStatus Status { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>How much of the torrent is on disk and verified, from 0 to 1.</summary>
    public double Progress { get; set; }

    /// <summary>The client's current tracker URL for this torrent (may be empty).</summary>
    public string Tracker { get; set; } = string.Empty;

    /// <summary>v1 / v2 info-hashes (hybrid torrents have both; blank if the client doesn't report them).</summary>
    public string InfoHashV1 { get; set; } = string.Empty;

    public string InfoHashV2 { get; set; } = string.Empty;
}
