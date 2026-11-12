using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// One piece of work a scan does for a drive once its files have been read,
/// for example "Finding downloads to link".
/// </summary>
public sealed record ScanStep(
    string Name,
    Func<DriveProfile, Task> RunAsync);

/// <summary>
/// Work a scan does once, after every drive is done, for example "Taking
/// names from Sonarr and Radarr".
/// </summary>
public sealed record FinishingStep(
    string Name,
    Func<Task> RunAsync);

public interface IScanStepProvider
{
    IReadOnlyList<ScanStep> GetSteps();

    IReadOnlyList<FinishingStep> GetFinishingSteps() => [];
}

/// <summary>
/// A snapshot of what a running scan is doing right now.
/// </summary>
public sealed record ScanProgress
{
    public bool IsRunning { get; init; }

    public bool CancelRequested { get; init; }

    public string DriveName { get; init; } = string.Empty;

    public int DriveNumber { get; init; }

    public int DriveCount { get; init; }

    public string StepName { get; init; } = string.Empty;

    public int StepNumber { get; init; }

    public int StepCount { get; init; }

    public long FilesRead { get; init; }

    public DateTime? StartedUtc { get; init; }

    /// <summary>
    /// Rough share of the whole scan that is finished, 0 to 100.
    /// </summary>
    public int Percent { get; init; }
}

/// <summary>
/// How the most recent scan ended.
/// </summary>
public sealed class ScanSummary
{
    public DateTime StartedUtc { get; set; }

    public DateTime FinishedUtc { get; set; }

    public bool Cancelled { get; set; }

    public List<string> Drives { get; set; } = [];

    public long FilesRead { get; set; }

    /// <summary>
    /// Steps that failed, in plain words. A failed step does not stop the
    /// rest of the scan.
    /// </summary>
    public List<string> Problems { get; set; } = [];
}

/// <summary>
/// Runs the one scan the whole app shares. A scan reads each drive's
/// folders once and then lets every feature work from that single reading.
/// It belongs to the server, not to a browser tab: closing the page does not
/// stop it, and every open page sees the same progress.
/// </summary>
public interface IScanCoordinator
{
    ScanProgress Progress { get; }

    ScanSummary? LastSummary { get; }

    /// <summary>
    /// When this drive last finished a full scan, if it ever has.
    /// </summary>
    DateTime? GetLastScanUtc(string driveName);

    /// <summary>
    /// Starts scanning one drive, or every enabled drive when
    /// <paramref name="driveName"/> is blank. Returns false if a scan is
    /// already running.
    /// </summary>
    bool Start(string? driveName = null);

    /// <summary>
    /// Asks the running scan to stop. It stops reading files at once, but a
    /// step that is already analysing finishes first.
    /// </summary>
    void Cancel();

    /// <summary>
    /// Raised whenever <see cref="Progress"/> changes, including when a scan
    /// starts or ends. May be raised on any thread.
    /// </summary>
    event Action? Changed;

    /// <summary>
    /// Completes when the scan that is running now ends. Completed already
    /// if nothing is running.
    /// </summary>
    Task WhenIdleAsync();
}
