using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Where a running cross-seed scan has got to.
/// </summary>
public sealed record CrossSeedScanProgress(
    int Done,
    int Total,
    string CurrentTorrent,
    int Found);

/// <summary>
/// What happened when matches were added.
/// </summary>
public sealed record CrossSeedAddSummary(
    int Linked,
    int FailedToLink,
    int AddedToClient,
    int FailedToAddToClient);

/// <summary>
/// Cross-seeding: for everything already seeding in the client, looks on
/// the indexers for other torrents of the very same files, proves each one
/// byte-for-byte, and can then add it to the client pointing at hardlinks
/// of the files already on disk.
///
/// One scan runs at a time for the whole app and keeps running if the page
/// is closed, so the state lives here. Matches are kept in memory until the
/// next scan or a restart.
/// </summary>
public interface ICrossSeedScanService
{
    bool IsScanning { get; }

    /// <summary>Null when no scan is running.</summary>
    CrossSeedScanProgress? Progress { get; }

    /// <summary>How the last scan ended, as a sentence.</summary>
    string? LastMessage { get; }

    bool LastSucceeded { get; }

    /// <summary>
    /// Proven matches that haven't been added yet.
    /// </summary>
    IReadOnlyList<CrossSeedScanResult> Matches { get; }

    /// <summary>
    /// Indexer results left out because the same torrent is already in
    /// the client.
    /// </summary>
    IReadOnlyList<string> AlreadyInClient { get; }

    /// <summary>
    /// Raised (from the scan's own thread) whenever any of the above changes.
    /// </summary>
    event Action? Changed;

    /// <summary>
    /// Does nothing if a scan is already running.
    /// </summary>
    Task ScanAsync();

    void Stop();

    /// <summary>
    /// Hardlinks each match's files into the cross-seed folder under the
    /// names the new torrent expects, then adds the torrent to the client,
    /// stopped. A match that was added leaves <see cref="Matches"/>.
    /// </summary>
    Task<CrossSeedAddSummary> AddAsync(
        IReadOnlyCollection<CrossSeedScanResult> matches,
        Action<string>? onProgress = null);
}
