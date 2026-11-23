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
    /// Moves one torrent's seed onto its library's drive. Undoes what it
    /// did if the client's recheck doesn't pass. Afterwards it is left
    /// paused, or started again when <paramref name="startAfter"/> is set.
    /// </summary>
    Task<SeedMoveResult> MoveAsync(SeedMoveCandidate candidate, CancellationToken cancellationToken = default, bool startAfter = false);

    /// <summary>The run of moves under way or just finished, or null.</summary>
    SeedMoveBatch? Batch { get; }

    /// <summary>
    /// Moves these one after another in the background, so it carries on if
    /// the page is closed. False when a move or run is already going.
    /// </summary>
    bool StartBatch(IReadOnlyList<SeedMoveCandidate> candidates, bool startAfter);

    /// <summary>Stops the run once the move in progress finishes.</summary>
    void StopBatch();

    /// <summary>Forgets a finished run's summary.</summary>
    void ClearBatch();
}
