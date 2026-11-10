using Microsoft.AspNetCore.Components;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Web.Support;

/// <summary>
/// Base for pages that show the results of the app-wide scan. Scanning is
/// started from the Scan control at the top of every page, not from the
/// page itself, so all a results page needs is:
///
///   - <see cref="IsScanning"/>, to stop anything being acted on while the
///     list underneath it is being rebuilt, and
///   - <see cref="OnScanFinishedAsync"/>, to reload when a scan ends.
///
/// Use with <c>@inherits ScanAwareComponent</c>.
/// </summary>
public abstract class ScanAwareComponent : ComponentBase, IDisposable
{
    [Inject]
    protected IScanCoordinator Scans { get; set; } = default!;

    protected bool IsScanning { get; private set; }

    protected override void OnInitialized()
    {
        IsScanning = Scans.Progress.IsRunning;

        Scans.Changed += HandleScanChanged;
    }

    /// <summary>
    /// Called once each time a scan ends (finished or cancelled). Reload
    /// whatever the page shows, and drop anything half-selected or
    /// half-confirmed that may no longer exist.
    /// </summary>
    protected virtual Task OnScanFinishedAsync() =>
        Task.CompletedTask;

    private void HandleScanChanged()
    {
        // Raised from the scan's own thread - hop back onto this page's.
        _ = InvokeAsync(async () =>
        {
            var isRunning = Scans.Progress.IsRunning;

            if (isRunning == IsScanning)
            {
                return;
            }

            var justFinished = IsScanning && !isRunning;

            IsScanning = isRunning;

            if (justFinished)
            {
                await OnScanFinishedAsync();
            }

            StateHasChanged();
        });
    }

    public virtual void Dispose()
    {
        Scans.Changed -= HandleScanChanged;

        GC.SuppressFinalize(this);
    }
}
