using Microsoft.Extensions.Configuration;

namespace ChasePie.Lookout.Core;

/// <summary>
/// Names of the environment variables the services read. The AppHost sets them; see <c>example.env</c>.
/// </summary>
public static class LookoutEnvironmentVariables
{
    /// <summary>
    /// The directory watch scripts are saved to and run from.
    /// </summary>
    public const string WatchScriptsDir = "LOOKOUT_WATCH_SCRIPTS_DIR";

    /// <summary>
    /// The directory the Backend keeps its SQLite database files in.
    /// </summary>
    public const string DatabaseDir = "LOOKOUT_DATABASE_DIR";

    /// <summary>
    /// The Runner's base URL, used by the Backend.
    /// </summary>
    public const string RunnerUrl = "LOOKOUT_RUNNER_URL";

    /// <summary>
    /// The browserless container's base URL, used by the Runner.
    /// </summary>
    public const string BrowserlessUrl = "LOOKOUT_BROWSERLESS_URL";

    /// <summary>
    /// Reads a directory from configuration, makes it absolute, and creates it if it doesn't exist yet.
    /// </summary>
    /// <exception cref="InvalidOperationException">The variable is not set.</exception>
    public static string GetRequiredDirectory(IConfiguration configuration, string name)
    {
        var configuredDir = configuration[name];
        if (string.IsNullOrWhiteSpace(configuredDir))
        {
            throw new InvalidOperationException($"Environment variable {name} must be set");
        }

        var fullPath = Path.GetFullPath(configuredDir);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }
}
