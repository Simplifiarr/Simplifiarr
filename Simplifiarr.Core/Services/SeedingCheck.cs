using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public enum SeedState
{
    /// <summary>The file has no other name: changing it touches nothing else.</summary>
    NotLinked,

    /// <summary>Hardlinked elsewhere, but no torrent in the client uses any of its names.</summary>
    LinkedNotSeeding,

    /// <summary>A torrent in the client uses it: it must stay byte for byte the same.</summary>
    Seeding,

    /// <summary>Hardlinked, and the torrent client couldn't be asked: treated as seeding.</summary>
    Unknown
}

/// <summary>
/// Whether a file is really seeding. A hardlink alone doesn't say: the
/// other name may be a leftover in a downloads folder whose torrent was
/// removed long ago. Only a torrent in the client that uses one of the
/// file's names makes it seeding. Ask once per batch; it holds the
/// client's list of torrents.
/// </summary>
public sealed class SeedingCheck
{
    private readonly List<(string Path, string Name)>? _torrents;

    private SeedingCheck(List<(string Path, string Name)>? torrents)
    {
        _torrents = torrents;
    }

    /// <summary>For tests and callers with no torrent client set up.</summary>
    public static SeedingCheck From(IEnumerable<(string ContentPath, string Name)>? torrents) =>
        new(torrents?.Where(torrent => !string.IsNullOrWhiteSpace(torrent.ContentPath))
            .Select(torrent => (Path.TrimEndingDirectorySeparator(torrent.ContentPath), torrent.Name))
            .ToList());

    public static async Task<SeedingCheck> CreateAsync(ITorrentClient? client)
    {
        if (client is null)
        {
            return From(null);
        }

        var torrents = await client.TryGetTorrentsAsync();

        return From(torrents?.Select(torrent => (torrent.ContentPath, torrent.Name)));
    }

    public bool ClientReachable => _torrents is not null;

    /// <summary>The file's state, and its other names (empty unless hardlinked).</summary>
    public (SeedState State, IReadOnlyList<string> OtherNames, string? Torrent) Check(string path)
    {
        if (NativeHardlinks.GetIdentity(path) is not { LinkCount: > 1 })
        {
            return (SeedState.NotLinked, [], null);
        }

        var names = NativeHardlinks.GetLinkPaths(path);
        var others = names.Where(name => !string.Equals(name, path, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_torrents is null)
        {
            return (SeedState.Unknown, others, null);
        }

        foreach (var name in names)
        {
            foreach (var (content, torrent) in _torrents)
            {
                if (string.Equals(name, content, StringComparison.OrdinalIgnoreCase) || PathContainment.IsPathUnderRoot(name, content))
                {
                    return (SeedState.Seeding, others, torrent);
                }
            }
        }

        return (SeedState.LinkedNotSeeding, others, null);
    }
}
