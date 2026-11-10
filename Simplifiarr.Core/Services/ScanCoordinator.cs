using Simplifiarr.Core.Models;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Core.Services;

public class ScanCoordinator
    : IScanCoordinator
{
    private const string ReadingFilesStep = "Reading files";

    // Progress while reading files can change thousands of times a second;
    // pages only need to hear about it a few times a second.
    private static readonly TimeSpan MinimumNotifyGap =
        TimeSpan.FromMilliseconds(250);

    private readonly ISettingsService _settingsService;
    private readonly IFileInventoryService _fileInventory;
    private readonly IScanStepProvider _stepProvider;

    private readonly object _sync = new();
    private readonly string _statePath;

    private ScanState _state;
    private ScanProgress _progress = new();
    private CancellationTokenSource? _cancellation;
    private Task _running = Task.CompletedTask;
    private DateTime _lastNotifyUtc;

    public ScanCoordinator(
        ISettingsService settingsService,
        IFileInventoryService fileInventory,
        IScanStepProvider stepProvider)
    {
        _settingsService = settingsService;
        _fileInventory = fileInventory;
        _stepProvider = stepProvider;

        _statePath = AppPaths.DataFile("scan-state.json");
        _state = JsonFileStore.Load<ScanState>(_statePath) ?? new ScanState();
    }

    public event Action? Changed;

    public ScanProgress Progress
    {
        get
        {
            lock (_sync)
            {
                return _progress;
            }
        }
    }

    public ScanSummary? LastSummary
    {
        get
        {
            lock (_sync)
            {
                return _state.LastSummary;
            }
        }
    }

    public DateTime? GetLastScanUtc(string driveName)
    {
        lock (_sync)
        {
            return _state.LastScanUtcByDrive.TryGetValue(driveName, out var value)
                ? value
                : null;
        }
    }

    public Task WhenIdleAsync()
    {
        lock (_sync)
        {
            return _running;
        }
    }

    public bool Start(string? driveName = null)
    {
        lock (_sync)
        {
            if (_progress.IsRunning)
            {
                return false;
            }

            _cancellation = new CancellationTokenSource();

            _progress = new ScanProgress
            {
                IsRunning = true,
                StepName = "Starting",
                StartedUtc = DateTime.UtcNow
            };

            var token = _cancellation.Token;

            _running = Task.Run(() => RunAsync(driveName, token));
        }

        Notify(force: true);

        return true;
    }

    public void Cancel()
    {
        lock (_sync)
        {
            if (!_progress.IsRunning || _cancellation is null)
            {
                return;
            }

            _cancellation.Cancel();

            _progress = _progress with { CancelRequested = true };
        }

        Notify(force: true);
    }

    private async Task RunAsync(
        string? driveName,
        CancellationToken token)
    {
        var summary = new ScanSummary { StartedUtc = DateTime.UtcNow };

        try
        {
            var profiles =
                (await _settingsService.GetEnabledDriveProfilesAsync())
                    .Where(profile =>
                        string.IsNullOrWhiteSpace(driveName) ||
                        string.Equals(
                            profile.Name,
                            driveName,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();

            var steps = _stepProvider.GetSteps();

            for (var index = 0; index < profiles.Count; index++)
            {
                token.ThrowIfCancellationRequested();

                // A drive whose folders overlap still gets scanned, but its
                // results can't be trusted - say so where the user will see it.
                foreach (var problem in DriveProfileValidator.GetProblems(profiles[index]))
                {
                    summary.Problems.Add($"{profiles[index].Name} setup: {problem}");
                }

                var filesBefore = summary.FilesRead;

                summary.FilesRead +=
                    await ScanDriveAsync(
                        profiles[index],
                        index + 1,
                        profiles.Count,
                        steps,
                        filesBefore,
                        summary,
                        token);

                summary.Drives.Add(profiles[index].Name);

                lock (_sync)
                {
                    _state.LastScanUtcByDrive[profiles[index].Name] =
                        DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            summary.Cancelled = true;
        }
        catch (Exception exception)
        {
            AppLog.Error("The scan stopped unexpectedly.", exception);

            summary.Problems.Add(
                $"The scan stopped unexpectedly: {exception.Message}");
        }

        summary.FinishedUtc = DateTime.UtcNow;

        lock (_sync)
        {
            _state.LastSummary = summary;

            JsonFileStore.Save(_statePath, _state);

            _progress = new ScanProgress();

            _cancellation?.Dispose();
            _cancellation = null;
        }

        AppLog.Info(
            $"Scan {(summary.Cancelled ? "cancelled" : "finished")}: " +
            $"{summary.Drives.Count} drive(s), {summary.FilesRead:N0} files, " +
            $"{(summary.FinishedUtc - summary.StartedUtc).TotalSeconds:N0}s, " +
            $"{summary.Problems.Count} problem(s).");

        Notify(force: true);
    }

    private async Task<long> ScanDriveAsync(
        DriveProfile profile,
        int driveNumber,
        int driveCount,
        IReadOnlyList<ScanStep> steps,
        long filesReadSoFar,
        ScanSummary summary,
        CancellationToken token)
    {
        // "Reading files" counts as one step alongside the analysis steps.
        var stepCount = steps.Count + 1;
        long filesOnThisDrive = 0;

        void Report(string stepName, int stepNumber)
        {
            lock (_sync)
            {
                _progress = _progress with
                {
                    DriveName = profile.Name,
                    DriveNumber = driveNumber,
                    DriveCount = driveCount,
                    StepName = stepName,
                    StepNumber = stepNumber,
                    StepCount = stepCount,
                    FilesRead = filesReadSoFar + filesOnThisDrive,
                    Percent = PercentDone(
                        driveNumber,
                        driveCount,
                        stepNumber,
                        stepCount)
                };
            }

            Notify(force: true);
        }

        using var session =
            _fileInventory.BeginScanSession(
                token,
                count =>
                {
                    filesOnThisDrive = count;

                    lock (_sync)
                    {
                        _progress = _progress with
                        {
                            FilesRead = filesReadSoFar + count
                        };
                    }

                    Notify(force: false);
                });

        Report(ReadingFilesStep, 1);

        var stepTimer = System.Diagnostics.Stopwatch.StartNew();

        // Shortest paths first: a folder that sits inside one already read
        // is answered from that reading instead of touching the disk again.
        foreach (var root in GetRoots(profile))
        {
            token.ThrowIfCancellationRequested();

            await _fileInventory.GetInventoryAsync(root, forceRefresh: true);
        }

        // Step timings go to the log so a slow scan can be explained
        // afterwards instead of guessed at.
        AppLog.Info(
            $"Scan {profile.Name}: {ReadingFilesStep} - {filesOnThisDrive:N0} files in {stepTimer.Elapsed.TotalSeconds:N1}s.");

        for (var index = 0; index < steps.Count; index++)
        {
            token.ThrowIfCancellationRequested();

            Report(steps[index].Name, index + 2);

            stepTimer.Restart();

            try
            {
                await steps[index].RunAsync(profile);

                AppLog.Info(
                    $"Scan {profile.Name}: {steps[index].Name} - {stepTimer.Elapsed.TotalSeconds:N1}s.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One feature failing must not cost the user the results of
                // all the others.
                AppLog.Error(
                    $"Scan step '{steps[index].Name}' failed on {profile.Name}.",
                    exception);

                summary.Problems.Add(
                    $"{profile.Name}: \"{steps[index].Name}\" failed - {exception.Message}");
            }
        }

        return filesOnThisDrive;
    }

    private static List<string> GetRoots(DriveProfile profile)
    {
        return new[]
            {
                profile.DownloadPath,
                profile.MovieLibraryPath,
                profile.TvLibraryPath,
                profile.BookLibraryPath,
                profile.AudiobookLibraryPath,
                profile.AudiobookDownloadPath,
                profile.MusicLibraryPath,
                profile.MusicDownloadPath,
                profile.ComicLibraryPath,
                profile.ComicDownloadPath
            }
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                Directory.Exists(path))
            .Select(path =>
                Path.GetFullPath(path)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .ToList();
    }

    private static int PercentDone(
        int driveNumber,
        int driveCount,
        int stepNumber,
        int stepCount)
    {
        if (driveCount == 0 || stepCount == 0)
        {
            return 0;
        }

        var finishedDrives = driveNumber - 1;
        var finishedSteps = stepNumber - 1;

        var fraction =
            (finishedDrives + (double)finishedSteps / stepCount) / driveCount;

        return (int)Math.Clamp(fraction * 100, 0, 99);
    }

    private void Notify(bool force)
    {
        if (!force)
        {
            var now = DateTime.UtcNow;

            lock (_sync)
            {
                if (now - _lastNotifyUtc < MinimumNotifyGap)
                {
                    return;
                }

                _lastNotifyUtc = now;
            }
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception exception)
        {
            // A page that fails to redraw must not break the scan.
            AppLog.Warn("A page failed while handling a scan update.", exception);
        }
    }

    private sealed class ScanState
    {
        public Dictionary<string, DateTime> LastScanUtcByDrive { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public ScanSummary? LastSummary { get; set; }
    }
}
