using Simplifiarr.Core.Services;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Web;

/// <summary>
/// Looks on GitHub for a new version twice a day (when that's on), and
/// installs it at the chosen time when automatic installs are on. Never
/// installs while a scan is running.
/// </summary>
public sealed class UpdateCheckService : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(12);
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    // How long after the chosen time an automatic install may still start.
    private static readonly TimeSpan InstallWindow = TimeSpan.FromHours(2);

    private readonly IUpdateService _updates;
    private readonly IScanCoordinator _scans;

    private DateOnly? _lastAutoInstallDay;

    public UpdateCheckService(IUpdateService updates, IScanCoordinator scans)
    {
        _updates = updates;
        _scans = scans;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

            using var timer = new PeriodicTimer(Tick);

            do
            {
                try
                {
                    await RunAsync();
                }
                catch (Exception exception)
                {
                    AppLog.Warn("Automatic update check failed.", exception);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync()
    {
        var state = _updates.State;

        if (!state.CheckAutomatically && !state.InstallAutomatically)
        {
            return;
        }

        if (state.LastCheckedUtc is not { } last || DateTime.UtcNow - last > CheckEvery)
        {
            await _updates.CheckAsync();
        }

        if (!state.InstallAutomatically || _updates.Available is null || !_updates.Install.CanUpdateItself || _scans.Progress.IsRunning)
        {
            return;
        }

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        if (_lastAutoInstallDay == today || !TimeOnly.TryParse(state.InstallTime, out var at))
        {
            return;
        }

        var start = today.ToDateTime(at);

        if (now >= start && now < start + InstallWindow)
        {
            // One try a day, so a failing install isn't repeated all night.
            _lastAutoInstallDay = today;

            if (await _updates.InstallAsync() is { } problem)
            {
                AppLog.Warn($"Automatic update didn't install: {problem}");
            }
        }
    }
}
