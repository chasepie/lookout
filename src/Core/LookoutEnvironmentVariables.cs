using Microsoft.Extensions.Configuration;

namespace ChasePie.Lookout.Core;

/// <summary>
/// Names of the environment variables the services read. The AppHost sets them: the ones the user chooses come from
/// <c>.env</c> and are listed in <c>example.env</c>; it works out or generates the rest. The Runner's image sets two of
/// its own: <see cref="WatchCliRoot"/>, and <see cref="WatchScriptsDir"/> as <c>/app/scripts</c>, where the AppHost mounts
/// the scripts directory.
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
    /// The key the Backend sends the Runner, and without which the Runner refuses requests. Both require it, at least
    /// <see cref="RunnerApiKeyMinLength"/> characters. The AppHost generates a new one on each start.
    /// </summary>
    public const string RunnerApiKey = "LOOKOUT_RUNNER_API_KEY";

    /// <summary>
    /// The shortest <see cref="RunnerApiKey"/> the Backend and the Runner accept.
    /// </summary>
    public const int RunnerApiKeyMinLength = 32;

    /// <summary>
    /// The directory whose <c>node_modules</c> holds the watch SDK and Playwright, which the Runner runs scripts with.
    /// The Runner's image sets it; without it, the Runner uses the repo root.
    /// </summary>
    public const string WatchCliRoot = "LOOKOUT_WATCH_CLI_ROOT";

    /// <summary>
    /// The browserless container's base URL, used by the Runner, with browserless's token in its <c>token</c> query
    /// parameter.
    /// </summary>
    public const string BrowserlessUrl = "LOOKOUT_BROWSERLESS_URL";

    /// <summary>
    /// The HTTP proxy that watches with <see cref="CreateWatchDto.UseVpn"/> send their browser traffic through.
    /// The Runner passes it to the browser, so its value must be reachable from wherever the browser runs.
    /// The Backend sends a request through it after a region switch, to tell when the new tunnel is up, so its
    /// value must be reachable from the Backend. The Backend requires it along with <see cref="GluetunUrl"/>.
    /// Its credentials go in the URL, as in <c>http://user:password@host:8888</c>.
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
    /// The Owner's username, which the Backend requires. The Owner is the one person who signs in to Lookout.
    /// </summary>
    public const string OwnerUsername = "LOOKOUT_OWNER_USERNAME";

    /// <summary>
    /// The Owner's password, which the Backend requires, at least <see cref="OwnerPasswordMinLength"/> characters.
    /// Changing it, or <see cref="OwnerUsername"/> other than its case, signs out every session once the Backend restarts.
    /// </summary>
    public const string OwnerPassword = "LOOKOUT_OWNER_PASSWORD";

    /// <summary>
    /// The shortest <see cref="OwnerPassword"/> the Backend and the AppHost accept.
    /// </summary>
    public const int OwnerPasswordMinLength = 16;

    /// <summary>
    /// The reverse proxies the Backend trusts the <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> headers from, as
    /// comma-separated IP addresses or CIDR networks, such as <c>172.30.0.1, 10.0.0.0/24</c>. Without it, the Backend trusts
    /// none and ignores both headers.
    /// </summary>
    public const string TrustedProxies = "LOOKOUT_TRUSTED_PROXIES";

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
