using System.Globalization;
using Simplifiarr.Core.Models;

namespace Simplifiarr.Core.Services;

/// <summary>
/// Works out when the next automatic scan is due. Times are local, the way
/// they are typed into Settings.
/// </summary>
public static class AutoScanSchedule
{
    public static readonly int[] HourChoices = [1, 2, 3, 6, 12, 24];

    /// <summary>
    /// When the next automatic scan should start, or null when automatic
    /// scanning is off. A time at or before <paramref name="nowLocal"/>
    /// means it is due now, for example when the computer was off at the
    /// set time.
    /// </summary>
    /// <param name="lastScanLocal">When the last scan of any kind started.</param>
    public static DateTime? NextRunLocal(
        SimplifiarrSettings settings,
        DateTime? lastScanLocal,
        DateTime nowLocal) =>
        NextRunLocal(settings.AutoScan, settings.AutoScanEveryHours, settings.AutoScanDailyTime, lastScanLocal, nowLocal);

    /// <summary>The same rules for any schedule: every few hours, or daily at a time.</summary>
    public static DateTime? NextRunLocal(
        AutoScanMode mode,
        int everyHours,
        string dailyTime,
        DateTime? lastScanLocal,
        DateTime nowLocal)
    {
        switch (mode)
        {
            case AutoScanMode.EveryHours:
            {
                var hours = Math.Clamp(everyHours, 1, 24 * 31);

                return lastScanLocal is { } last
                    ? last.AddHours(hours)
                    : nowLocal;
            }

            case AutoScanMode.Daily:
            {
                var time = ParseTime(dailyTime) ?? new TimeSpan(3, 0, 0);

                // The most recent set time that has already passed.
                var latest = nowLocal.Date + time;

                if (latest > nowLocal)
                {
                    latest = latest.AddDays(-1);
                }

                return lastScanLocal is { } last && last >= latest
                    ? latest.AddDays(1)
                    : lastScanLocal is null
                        ? latest.AddDays(1)
                        : latest;
            }

            default:
                return null;
        }
    }

    /// <summary>"03:00", "3:00" or "15:30"; null if it isn't a time.</summary>
    public static TimeSpan? ParseTime(string? text)
    {
        return TimeSpan.TryParseExact(
                   text?.Trim(),
                   ["h\\:mm", "hh\\:mm"],
                   CultureInfo.InvariantCulture,
                   out var time) &&
               time < TimeSpan.FromDays(1)
            ? time
            : null;
    }
}
