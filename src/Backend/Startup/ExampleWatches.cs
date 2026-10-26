using ChasePie.Lookout.Backend.Scheduling;
using ChasePie.Lookout.Database;
using ChasePie.Lookout.Model;

namespace ChasePie.Lookout.Backend.Startup;

/// <summary>
/// Gives a new development database a Watch to try Runs with: <c>examples/bot-detection.ts</c>, which reads
/// browserscan.net's bot-detection verdict.
/// </summary>
public static class ExampleWatches
{
    public static async Task SeedAsync(LookoutDbContext database, WatchSchedules schedules)
    {
        using var script = typeof(ExampleWatches).Assembly.GetManifestResourceStream("ExampleScripts/bot-detection.ts")
            ?? throw new InvalidOperationException("The bot-detection example Script is not embedded in the backend.");
        using var reader = new StreamReader(script);
        // The Watch and its current Script version refer to each other, so the version is saved before it is current.
        await using var transaction = await database.Database.BeginTransactionAsync();

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
        if (await schedules.ScheduleFirstRunAsync(watch, database, CancellationToken.None) is { } problem)
        {
            throw new InvalidOperationException($"The example Watch's schedule can't be used: {problem}");
        }

        var version = new ScriptVersion
        {
            WatchId = watch.Id,
            Source = await reader.ReadToEndAsync(),
            FileKind = "ts"
        };
        watch.ScriptVersions.Add(version);

        database.Watches.Add(watch);
        await database.SaveChangesAsync();
        watch.CurrentScriptVersionId = version.Id;
        await database.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
