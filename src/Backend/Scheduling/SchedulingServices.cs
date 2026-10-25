using ChasePie.Lookout.Backend.Running;

namespace ChasePie.Lookout.Backend.Scheduling;

public static class SchedulingServices
{
    /// <summary>Watches' schedules, the Run slots they take turns at (<c>Scheduling:MaxConcurrentRuns</c>), and the Scheduler.</summary>
    public static void AddScheduling(this IServiceCollection services, IConfiguration configuration)
    {
        var timeZoneId = configuration["Scheduling:TimeZone"];
        TimeZoneInfo? timeZone = null;
        if (!string.IsNullOrWhiteSpace(timeZoneId) && !TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId.Trim(), out timeZone))
        {
            throw new InvalidOperationException($"Scheduling:TimeZone must be a time zone ID such as Europe/London, not '{timeZoneId}'");
        }
        services.AddSingleton(provider => new WatchSchedules(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<TimeProvider>(), timeZone));

        var maxConcurrentRuns = configuration.GetValue("Scheduling:MaxConcurrentRuns", 2);
        if (maxConcurrentRuns < 1)
        {
            throw new InvalidOperationException($"Scheduling:MaxConcurrentRuns must be at least 1, not {maxConcurrentRuns}");
        }
        // Before the Scheduler, as hosted services stop in reverse: the Scheduler stops starting Runs, then Runs drains.
        services.AddSingleton(provider => ActivatorUtilities.CreateInstance<Runs>(provider, maxConcurrentRuns));
        services.AddHostedService(provider => provider.GetRequiredService<Runs>());
        // Off only in tests, which start due Runs themselves so nothing races them.
        var polls = configuration.GetValue("Scheduling:Poll", true);
        services.AddSingleton(provider => ActivatorUtilities.CreateInstance<Scheduler>(provider, polls));
        services.AddHostedService(provider => provider.GetRequiredService<Scheduler>());
    }
}
