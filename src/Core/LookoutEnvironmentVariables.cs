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
    /// The HTTP proxy that watches with <see cref="CreateWatchDto.UseVpn"/> send their browser traffic through.
    /// The Runner passes it to the browser, so its value must be reachable from wherever the browser runs.
    /// The Backend sends a request through it after a region switch, to tell when the new tunnel is up, so its
    /// value must be reachable from the Backend. The Backend requires it along with <see cref="GluetunUrl"/>.
    /// </summary>
    public const string VpnProxyUrl = "LOOKOUT_VPN_PROXY_URL";

    /// <summary>
    /// The base URL of gluetun's HTTP control server, used by the Backend to switch VPN regions.
    /// Without it the Backend's VPN features are off.
    /// </summary>
    public const string GluetunUrl = "LOOKOUT_GLUETUN_URL";

    /// <summary>
    /// The API key gluetun's control server requires, sent in the <c>X-API-Key</c> header.
    /// </summary>
    public const string GluetunApiKey = "LOOKOUT_GLUETUN_API_KEY";

    /// <summary>
    /// The host directory mounted as gluetun's <c>/gluetun</c> data directory. The Backend reads the VPN
    /// servers list from its <c>servers</c> folder to know which regions it can switch to, and requires it along
    /// with <see cref="GluetunUrl"/>.
    /// </summary>
    public const string GluetunDir = "LOOKOUT_GLUETUN_DIR";

    /// <summary>
    /// Reads a base URL from configuration, adding a trailing slash so relative request URIs append to its path.
    /// </summary>
    /// <returns>The URL, or <see langword="null"/> if the variable is not set.</returns>
    /// <exception cref="InvalidOperationException">The variable is not an absolute URL.</exception>
    public static Uri? GetBaseUrl(IConfiguration configuration, string name)
    {
        var value = configuration[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value.EndsWith('/') ? value : value + "/", UriKind.Absolute, out var url)
            ? url
            : throw new InvalidOperationException($"Environment variable {name} must be an absolute URL");
    }

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
