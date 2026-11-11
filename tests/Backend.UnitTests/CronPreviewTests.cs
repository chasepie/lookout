using ChasePie.Lookout.Backend.Controllers;
using ChasePie.Lookout.Core;
using Microsoft.Extensions.Time.Testing;

namespace ChasePie.Lookout.Backend.UnitTests;

[TestClass]
public sealed class CronPreviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 10, 0, TimeSpan.Zero);

    private static readonly DateTime[] NextQuarterHours =
    [
        new(2026, 11, 2, 12, 15, 0, DateTimeKind.Utc),
        new(2026, 11, 2, 12, 30, 0, DateTimeKind.Utc),
        new(2026, 11, 2, 12, 45, 0, DateTimeKind.Utc),
        new(2026, 11, 2, 13, 0, 0, DateTimeKind.Utc),
        new(2026, 11, 2, 13, 15, 0, DateTimeKind.Utc),
    ];

    private static CronPreview Preview(string expression) =>
        new CronController(new FakeTimeProvider(Now)).Preview(expression);

    [TestMethod]
    public void ListsTheNextRunsAfterNow()
    {
        // Every 15 minutes falls on the same instants in every time zone, whatever the test machine's is
        var preview = Preview("*/15 * * * *");

        Assert.IsTrue(preview.IsValid);
        Assert.AreSequenceEqual(NextQuarterHours, preview.NextRuns.ToArray());
        Assert.AreEqual(TimeZoneInfo.Local.Id, preview.TimeZone);
    }

    [TestMethod]
    public void ReadsFieldsSeparatedByTabs()
    {
        Assert.IsTrue(Preview("*/15\t*\t*\t*\t*").IsValid);
    }

    [TestMethod]
    public void AnExpressionThatNeverRunsIsValidWithNoRuns()
    {
        var preview = Preview("0 0 30 2 *");

        Assert.IsTrue(preview.IsValid);
        Assert.IsEmpty(preview.NextRuns);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("0 9 * *")]
    [DataRow("0 0 9 * * 1-5")]
    [DataRow("60 * * * *")]
    [DataRow("0 9 * * 1,")]
    [DataRow("not a cron expression")]
    public void RefusesWhatAWatchCannotBeSavedWith(string expression)
    {
        var preview = Preview(expression);

        Assert.IsFalse(preview.IsValid);
        Assert.IsEmpty(preview.NextRuns);
        var watch = new CreateWatchDto
        {
            Name = "test",
            ScheduleEnabled = true,
            ScheduleKind = WatchScheduleKind.Cron,
            ScheduleInterval = expression,
        };
        Assert.IsNotEmpty(watch.Validate(new(watch)).ToList());
    }
}
