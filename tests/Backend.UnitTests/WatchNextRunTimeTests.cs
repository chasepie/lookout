using ChasePie.Lookout.Core;

namespace Backend.UnitTests;

[TestClass]
public sealed class WatchNextRunTimeTests
{
    private static readonly DateTime LastRun = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private static Watch ScheduledWatch(WatchScheduleKind kind, string interval) => new()
    {
        Name = "test",
        ScheduleEnabled = true,
        ScheduleKind = kind,
        ScheduleInterval = interval,
    };

    [TestMethod]
    [DataRow(WatchScheduleKind.Minutely, "5", 5 * 60)]
    [DataRow(WatchScheduleKind.Hourly, "2", 2 * 60 * 60)]
    [DataRow(WatchScheduleKind.Daily, "1", 24 * 60 * 60)]
    [DataRow(WatchScheduleKind.Weekly, "2", 14 * 24 * 60 * 60)]
    public void IntervalScheduleIsDueThatLongAfterLastRun(WatchScheduleKind kind, string interval, int expectedSeconds)
    {
        var next = ScheduledWatch(kind, interval).GetNextRunTime(LastRun, TimeZoneInfo.Utc);

        Assert.AreEqual(LastRun.AddSeconds(expectedSeconds), next);
    }

    [TestMethod]
    public void CronScheduleIsDueAtNextOccurrenceAfterLastRun()
    {
        var next = ScheduledWatch(WatchScheduleKind.Cron, "30 * * * *").GetNextRunTime(LastRun, TimeZoneInfo.Utc);

        Assert.AreEqual(new DateTime(2026, 11, 2, 12, 30, 0, DateTimeKind.Utc), next);
    }

    [TestMethod]
    public void CronScheduleUsesTheGivenTimeZone()
    {
        // 09:00 at UTC+05:00 is 04:00 UTC, so the next occurrence after 12:00 UTC is the following day
        var timeZone = TimeZoneInfo.CreateCustomTimeZone("UTC+5", TimeSpan.FromHours(5), "UTC+5", "UTC+5");

        var next = ScheduledWatch(WatchScheduleKind.Cron, "0 9 * * *").GetNextRunTime(LastRun, timeZone);

        Assert.AreEqual(new DateTime(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc), next);
    }

    [TestMethod]
    public void UnspecifiedKindIsTreatedAsUtc()
    {
        // SQLite returns stored DateTimes with an unspecified kind, which Cronos would reject
        var lastRun = DateTime.SpecifyKind(LastRun, DateTimeKind.Unspecified);

        var next = ScheduledWatch(WatchScheduleKind.Cron, "30 * * * *").GetNextRunTime(lastRun, TimeZoneInfo.Utc);

        Assert.AreEqual(new DateTime(2026, 11, 2, 12, 30, 0, DateTimeKind.Utc), next);
    }

    [TestMethod]
    public void DisabledScheduleIsNeverDue()
    {
        var watch = ScheduledWatch(WatchScheduleKind.Minutely, "5");
        watch.ScheduleEnabled = false;

        Assert.IsNull(watch.GetNextRunTime(LastRun, TimeZoneInfo.Utc));
    }

    [TestMethod]
    [DataRow(WatchScheduleKind.Minutely, "0")]
    [DataRow(WatchScheduleKind.Hourly, "soon")]
    [DataRow(WatchScheduleKind.Cron, "not a cron")]
    public void InvalidIntervalIsNeverDue(WatchScheduleKind kind, string interval)
    {
        Assert.IsNull(ScheduledWatch(kind, interval).GetNextRunTime(LastRun, TimeZoneInfo.Utc));
    }
}
