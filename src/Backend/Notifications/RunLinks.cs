using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Backend.Notifications;

/// <summary>
/// Links to runs' pages, for notifications, from <see cref="EnvVars.LookoutPublicUrl"/>.
/// </summary>
/// <param name="publicUrl">Where the Owner opens Lookout, ending in a slash, or <see langword="null"/> if unknown.</param>
public sealed class RunLinks(Uri? publicUrl)
{
    /// <exception cref="InvalidOperationException">The setting is not an absolute URL.</exception>
    public static RunLinks FromConfiguration(IConfiguration configuration) =>
        new(EnvVars.GetBaseUrl(configuration, EnvVars.LookoutPublicUrl));

    /// <summary>
    /// The run's page in the frontend, or <see langword="null"/> if Lookout doesn't know its public address.
    /// </summary>
    public Uri? For(WatchRun run) =>
        publicUrl is null ? null : new Uri(publicUrl, $"watches/{run.WatchId}/runs/{run.Id}");
}
