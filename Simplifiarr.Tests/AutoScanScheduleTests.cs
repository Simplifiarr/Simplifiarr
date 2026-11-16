using System.Text.Json;
using Simplifiarr.Core.Models;
using Simplifiarr.Core.Services;

namespace Simplifiarr.Tests;

[TestClass]
public class AutoScanScheduleTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 14, 0, 0);

    private static SimplifiarrSettings Every(int hours) =>
        new() { AutoScan = AutoScanMode.EveryHours, AutoScanEveryHours = hours };

    private static SimplifiarrSettings Daily(string time) =>
        new() { AutoScan = AutoScanMode.Daily, AutoScanDailyTime = time };

    [TestMethod]
    public void Off_never_runs()
    {
        Assert.IsNull(AutoScanSchedule.NextRunLocal(new SimplifiarrSettings(), Now.AddDays(-30), Now));
    }

    [TestMethod]
    public void Every_few_hours_counts_from_the_last_scan()
    {
        Assert.AreEqual(Now.AddHours(4), AutoScanSchedule.NextRunLocal(Every(6), Now.AddHours(-2), Now));
        Assert.IsTrue(AutoScanSchedule.NextRunLocal(Every(6), Now.AddHours(-7), Now) <= Now, "Overdue: runs now.");
        Assert.AreEqual(Now, AutoScanSchedule.NextRunLocal(Every(6), null, Now), "Never scanned: runs now.");
    }

    [TestMethod]
    public void Daily_runs_at_the_set_time_once()
    {
        // Scanned at 3:00 today; next is 3:00 tomorrow.
        Assert.AreEqual(
            new DateTime(2026, 3, 11, 3, 0, 0),
            AutoScanSchedule.NextRunLocal(Daily("03:00"), new DateTime(2026, 3, 10, 3, 0, 5), Now));

        // Later today, not yet reached.
        Assert.AreEqual(
            new DateTime(2026, 3, 10, 22, 30, 0),
            AutoScanSchedule.NextRunLocal(Daily("22:30"), new DateTime(2026, 3, 9, 22, 30, 0), Now));
    }

    [TestMethod]
    public void A_daily_scan_missed_while_the_computer_was_off_is_due_at_once()
    {
        var next = AutoScanSchedule.NextRunLocal(Daily("03:00"), new DateTime(2026, 3, 9, 3, 0, 0), Now);

        Assert.AreEqual(new DateTime(2026, 3, 10, 3, 0, 0), next);
        Assert.IsTrue(next <= Now);
    }

    [TestMethod]
    public void Daily_with_no_scan_yet_waits_for_the_set_time()
    {
        Assert.AreEqual(
            new DateTime(2026, 3, 11, 3, 0, 0),
            AutoScanSchedule.NextRunLocal(Daily("03:00"), null, Now));
    }

    [TestMethod]
    public void A_bad_time_falls_back_to_3am()
    {
        Assert.IsNull(AutoScanSchedule.ParseTime("25:00"));
        Assert.IsNull(AutoScanSchedule.ParseTime("soon"));
        Assert.AreEqual(new TimeSpan(7, 5, 0), AutoScanSchedule.ParseTime("7:05"));

        Assert.AreEqual(
            new DateTime(2026, 3, 11, 3, 0, 0),
            AutoScanSchedule.NextRunLocal(Daily("soon"), new DateTime(2026, 3, 10, 3, 0, 0), Now));
    }

    [TestMethod]
    public void Old_settings_files_have_it_off()
    {
        var settings = JsonSerializer.Deserialize<SimplifiarrSettings>("{}")!;

        Assert.AreEqual(AutoScanMode.Off, settings.AutoScan);
        Assert.Contains("\"AutoScan\":\"Off\"", JsonSerializer.Serialize(settings));
    }
}
