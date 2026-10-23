using ChasePie.Lookout.Backend.Scheduling;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend;

/// <summary>
/// Gives a new development database a Watch to try Runs with: <c>examples/bot-detection.ts</c>, which reads
/// browserscan.net's bot-detection verdict.
/// </summary>
public static class ExampleWatches
{
    public static void Seed(LookoutDbContext database, WatchSchedules schedules)
    {
        using var script = typeof(ExampleWatches).Assembly.GetManifestResourceStream("ExampleScripts/bot-detection.ts")
            ?? throw new InvalidOperationException("The bot-detection example Script is not embedded in the backend.");
        using var reader = new StreamReader(script);

        var watch = new Watch
        {
            Name = "Bot detection example",
            NotificationUrl = "https://www.browserscan.net/bot-detection",
            Schedule = new Schedule
            {
                Kind = ScheduleKind.Interval,
                Every = 1,
                Unit = IntervalUnit.Hours
            }
        };
        if (schedules.Schedule(watch) is { } problem)
        {
            throw new InvalidOperationException($"The example Watch's schedule can't be used: {problem}");
        }
        var version = new ScriptVersion
        {
            WatchId = watch.Id,
            Source = reader.ReadToEnd(),
            FileKind = "ts"
        };
        watch.ScriptVersions.Add(version);

        // The Watch and its current Script version refer to each other, so the version is saved before it is current.
        using var transaction = database.Database.BeginTransaction();
        database.Watches.Add(watch);
        database.SaveChanges();
        watch.CurrentScriptVersionId = version.Id;
        database.SaveChanges();
        transaction.Commit();
    }
}
