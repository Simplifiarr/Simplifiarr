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

    private DateTime? _lastAutoStartLocal;

    public AutoScanService(
        ISettingsService settings,
        IScanCoordinator scans,
        ICrossSeedScanService crossSeed)
    {
        _settings = settings;
        _scans = scans;
        _crossSeed = crossSeed;
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
