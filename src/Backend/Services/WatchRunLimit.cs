using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.Services;

/// <summary>
/// How many runs <see cref="WatchRunWorker"/> runs at once at most, from <see cref="EnvVars.LookoutMaxConcurrentRuns"/>.
/// </summary>
public sealed record WatchRunLimit(int MaxConcurrentRuns)
{
    /// <exception cref="InvalidOperationException">The setting isn't a whole number of at least 1.</exception>
    public static WatchRunLimit FromConfiguration(IConfiguration configuration) =>
        new(EnvVars.ParseMaxConcurrentRuns(configuration[EnvVars.LookoutMaxConcurrentRuns]));
}
