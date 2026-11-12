using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Finds torrents seeding on one drive whose files were copied into a
/// library on another, and moves the seed onto the library's drive so the
/// two share one copy.
/// </summary>
public interface ISeedMoveService
{
    /// <summary>Raised whenever a move starts, makes progress or ends.</summary>
    event Action? Changed;

    /// <summary>The move in progress, or null.</summary>
    SeedMoveProgress? Current { get; }

    IReadOnlyList<SeedMoveCandidate> GetCandidates(string driveName);

    DateTime? GetLastScanUtc(string driveName);

    /// <summary>
    /// Looks at the torrents seeding from this drive. Only sizes are
    /// compared here; nothing is read or changed.
    /// </summary>
    Task ScanAsync(string driveName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves one torrent's seed onto its library's drive and leaves it
    /// paused. Undoes what it did if qBittorrent's recheck doesn't pass.
    /// </summary>
    Task<SeedMoveResult> MoveAsync(SeedMoveCandidate candidate, CancellationToken cancellationToken = default);
}
