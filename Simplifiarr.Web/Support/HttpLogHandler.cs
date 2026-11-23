using System.Diagnostics;
using Simplifiarr.Core.Utilities;

namespace Simplifiarr.Web.Support;

/// <summary>
/// Writes one line for each call Simplifiarr makes to Radarr, Sonarr,
/// qBittorrent, Prowlarr, Jellyfin and the rest: always when it fails, and
/// for every call while detailed logging is on. Query values (where API
/// keys often travel) are left out; only their names are kept.
/// </summary>
public sealed class HttpLogHandler(ILogger<HttpLogHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var what = $"{request.Method} {LogRedactor.Address(request.RequestUri)}";

        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if ((int)response.StatusCode >= 400)
            {
                logger.LogWarning("{What} answered {Status} {Reason} after {Ms:0} ms", what, (int)response.StatusCode, response.ReasonPhrase, took);
            }
            else if (LogSwitch.IsDetailed)
            {
                logger.LogDebug("{What} answered {Status} after {Ms:0} ms", what, (int)response.StatusCode, took);
            }

            return response;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            var took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            logger.LogWarning("{What} failed after {Ms:0} ms: {Error}", what, took, exception.Message);

            throw;
        }
    }
}
