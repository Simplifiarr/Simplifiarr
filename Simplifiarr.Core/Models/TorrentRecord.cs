namespace Simplifiarr.Core.Models;

/// <summary>
/// Where a torrent client saves: its default folder, and each category's
/// own folder (empty when the category uses the default folder plus its name).
/// </summary>
public sealed record TorrentClientLayout(string DefaultSavePath, IReadOnlyDictionary<string, string> Categories)
{
    /// <summary>The folder a category's torrents are saved to.</summary>
    public string FolderOf(string? category) =>
        string.IsNullOrWhiteSpace(category) || !Categories.TryGetValue(category, out var path)
            ? DefaultSavePath
            : string.IsNullOrWhiteSpace(path)
                ? System.IO.Path.Combine(DefaultSavePath, category)
                : path.Replace('/', System.IO.Path.DirectorySeparatorChar);
}

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

    /// <summary>Seeders connected right now, and in the whole swarm.</summary>
    public int SeedsConnected { get; set; }

    public int SeedsInSwarm { get; set; }

    /// <summary>Leechers connected right now, and in the whole swarm.</summary>
    public int LeechersConnected { get; set; }

    public int LeechersInSwarm { get; set; }

    /// <summary>Bytes per second.</summary>
    public long DownloadSpeed { get; set; }

    /// <summary>Time left to finish; null when unknown.</summary>
    public TimeSpan? TimeLeft { get; set; }

    /// <summary>Uploaded divided by downloaded.</summary>
    public double Ratio { get; set; }

    /// <summary>How long it has seeded in total.</summary>
    public TimeSpan SeedingTime { get; set; }

    /// <summary>The client's tags, comma separated.</summary>
    public string Tags { get; set; } = string.Empty;
}
