using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Web;

/// <summary>
/// Starts Scan by itself on the schedule set in Settings, and afterwards a
/// cross-seed search if that is ticked too. It only finds things: nothing
/// is moved, linked or deleted without you.
/// </summary>
public sealed class AutoScanService : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);

    // Lets the service settle after starting before a missed scan catches up.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly ISettingsService _settings;
    private readonly IScanCoordinator _scans;
    private readonly ICrossSeedScanService _crossSeed;
    private readonly ISizeReductionCandidateService _sizeReduction;

    private DateTime? _lastAutoStartLocal;

    // When an automatic search last started per drive, so one that fails
    // isn't retried every half-minute.
    private readonly Dictionary<string, DateTime> _sizeReductionAttempts = new(StringComparer.OrdinalIgnoreCase);

    public AutoScanService(
        ISettingsService settings,
        IScanCoordinator scans,
        ICrossSeedScanService crossSeed,
        ISizeReductionCandidateService sizeReduction)
    {
        _settings = settings;
        _scans = scans;
        _crossSeed = crossSeed;
        _sizeReduction = sizeReduction;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            using var timer = new PeriodicTimer(CheckEvery);

            do
            {
                try
                {
                    await RunIfDueAsync(stoppingToken);
                    await SearchSizeReductionIfDueAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    AppLog.Warn("Automatic scan failed.", exception);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // The app is shutting down.
        }
    }

    private async Task RunIfDueAsync(CancellationToken stoppingToken)
    {
        if (_scans.Progress.IsRunning || _crossSeed.IsScanning)
        {
            return;
        }

        var settings = await _settings.GetSettingsAsync();

        var now = DateTime.Now;

        var next =
            AutoScanSchedule.NextRunLocal(settings, LastScanLocal(), now);

        if (next is null || next > now)
        {
            return;
        }

        if (!_scans.Start())
        {
            return;
        }

        _lastAutoStartLocal = now;

        AppLog.Info("Automatic scan started.");

        await _scans.WhenIdleAsync().WaitAsync(stoppingToken);

        if (settings.AutoScanCrossSeed &&
            !string.IsNullOrWhiteSpace(settings.ProwlarrApiKey))
        {
            AppLog.Info("Automatic cross-seed search started.");

            await _crossSeed.ScanAsync().WaitAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Searches the indexers for smaller releases on each chosen drive
    /// whose turn has come, one drive at a time. Results are saved as if
    /// you had pressed Search on the Size Reduction page.
    /// </summary>
    private async Task SearchSizeReductionIfDueAsync(CancellationToken stoppingToken)
    {
        var settings = await _settings.GetSettingsAsync();

        if (settings.SizeReductionAuto == Simplifiarr.Core.Models.AutoScanMode.Off ||
            string.IsNullOrWhiteSpace(settings.ProwlarrApiKey))
        {
            return;
        }

        var drives =
            settings.DriveProfiles
                .Where(profile => profile.Enabled)
                .Where(profile =>
                    settings.SizeReductionDrives.Count == 0 ||
                    settings.SizeReductionDrives.Contains(profile.Name, StringComparer.OrdinalIgnoreCase))
                .Select(profile => profile.Name)
                .ToList();

        foreach (var drive in drives)
        {
            if (stoppingToken.IsCancellationRequested ||
                _scans.Progress.IsRunning || _crossSeed.IsScanning || _sizeReduction.IsSearching)
            {
                return;
            }

            var lastSearch = _sizeReduction.GetLastSearchUtc(drive)?.ToLocalTime();

            if (_sizeReductionAttempts.TryGetValue(drive, out var attempt) && (lastSearch is null || attempt > lastSearch))
            {
                lastSearch = attempt;
            }

            var now = DateTime.Now;

            var next =
                AutoScanSchedule.NextRunLocal(
                    settings.SizeReductionAuto,
                    settings.SizeReductionEveryHours,
                    settings.SizeReductionDailyTime,
                    lastSearch,
                    now);

            if (next is null || next > now)
            {
                continue;
            }

            _sizeReductionAttempts[drive] = now;

            AppLog.Info($"Automatic Size Reduction search started for {drive}.");

            await _sizeReduction.SearchAsync(drive).WaitAsync(stoppingToken);
        }
    }

    /// <summary>
    /// The later of the last scan anyone started and the last one this
    /// service started, so a scan that ended oddly isn't retried every
    /// half-minute.
    /// </summary>
    private DateTime? LastScanLocal()
    {
        var lastSummary = _scans.LastSummary?.StartedUtc.ToLocalTime();

        return (lastSummary, _lastAutoStartLocal) switch
        {
            ({ } a, { } b) => a > b ? a : b,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null
        };
    }
}
