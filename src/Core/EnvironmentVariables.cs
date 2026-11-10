using Microsoft.Extensions.Configuration;

namespace ChasePie.Lookout.Core;

/// <summary>
/// Names of the environment variables the services read. In development the AppHost sets them: the ones you choose come
/// from <c>.env</c> and are listed in <c>example.env</c>; it works out or generates the rest. In production they come
/// from <c>deploy/docker-compose.yaml</c> and its <c>.env</c>.
/// The images set some of their own: the Runner's <see cref="WatchCliRoot"/>, and both <see cref="LookoutWatchScriptsDir"/> as
/// <c>/app/scripts</c>, where the scripts are mounted, and the Backend's <see cref="LookoutDatabaseDir"/>.
/// </summary>
public static class EnvVars
{
    /// <summary>
    /// The browserless container's base URL, used by the Runner. Its token is <see cref="BrowserlessToken"/>.
    /// </summary>
    public const string BrowserlessUrl = "BROWSERLESS_URL";

    /// <summary>
    /// browserless's token, which the Runner requires and adds to <see cref="BrowserlessUrl"/> as its <c>token</c> query
    /// parameter. A setting of its own, so the token is set once beside browserless's own and kept out of URLs that end
    /// up in logs. See <see cref="GetBrowserlessUrl"/>.
    /// </summary>
    public const string BrowserlessToken = "BROWSERLESS_TOKEN";

    /// <summary>
    /// The HTTP proxy that watches with <see cref="CreateWatchDto.UseVpn"/> send their browser traffic through.
    /// The Runner passes it to the browser, so its value must be reachable from wherever the browser runs.
    /// The Backend sends a request through it after a region switch, to tell when the new tunnel is up, so its
    /// value must be reachable from the Backend. The Backend requires it along with <see cref="GluetunUrl"/>.
    /// Its username goes in the URL, as in <c>http://user@host:8888</c>, and its password in
    /// <see cref="GluetunProxyPassword"/>. See <see cref="GetVpnProxyUrl"/>.
    /// </summary>
    public const string GluetunProxyUrl = "GLUETUN_PROXY_URL";

    /// <summary>
    /// The password of <see cref="GluetunProxyUrl"/>, which the Backend and the Runner add to its URL. A setting of its own,
    /// so the password is set once beside gluetun's own and kept out of URLs that end up in logs.
    /// </summary>
    public const string GluetunProxyPassword = "GLUETUN_PROXY_PASSWORD";

    /// <summary>
    /// The base URL of gluetun's HTTP control server, used by the Backend to switch VPN regions.
    /// Without it the Backend's VPN features are off.
    /// </summary>
    public const string GluetunUrl = "GLUETUN_URL";

    /// <summary>
    /// The API key gluetun's control server requires, sent in the <c>X-API-Key</c> header.
    /// </summary>
    public const string GluetunApiKey = "GLUETUN_API_KEY";

    /// <summary>
    /// gluetun's <c>/gluetun</c> data directory, as the Backend sees it. The Backend reads the VPN servers list from
    /// its <c>servers.json</c> to know which regions it can switch to, and requires it along with <see cref="GluetunUrl"/>.
    /// </summary>
    public const string GluetunDataDir = "GLUETUN_DATA_DIR";

    /// <summary>
    /// The VPN regions Lookout may use, comma-separated as in <c>DE Berlin,US Texas</c>; without it, every region in
    /// gluetun's servers list. gluetun gets it as its <c>SERVER_REGIONS</c>, so it connects to one of them when it starts.
    /// The Backend switches and rotates only among them, since each switch replaces gluetun's filter with the one region.
    /// </summary>
    public const string GluetunServerRegions = "GLUETUN_SERVER_REGIONS";

    /// <summary>
    /// The directory watch scripts are saved to and run from.
    /// </summary>
    public const string LookoutWatchScriptsDir = "LOOKOUT_WATCH_SCRIPTS_DIR";

    /// <summary>
    /// The directory the Backend keeps its SQLite database files in.
    /// </summary>
    public const string LookoutDatabaseDir = "LOOKOUT_DATABASE_DIR";

    /// <summary>
    /// The Runner's base URL, used by the Backend.
    /// </summary>
    public const string LookoutRunnerUrl = "LOOKOUT_RUNNER_URL";

    /// <summary>
    /// The key the Backend sends the Runner, and without which the Runner refuses requests. Both require it, at least
    /// <see cref="RunnerApiKeyMinLength"/> characters. The AppHost generates a new one on each start.
    /// </summary>
    public const string LookoutRunnerApiKey = "LOOKOUT_RUNNER_API_KEY";

    /// <summary>
    /// The shortest <see cref="LookoutRunnerApiKey"/> the Backend and the Runner accept.
    /// </summary>
    public const int RunnerApiKeyMinLength = 32;

    /// <summary>
    /// The directory whose <c>node_modules</c> holds the watch SDK and Playwright, which the Runner runs scripts with.
    /// The Runner's image sets it; without it, the Runner uses the repo root.
    /// </summary>
    public const string WatchCliRoot = "LOOKOUT_RUNNER_WATCH_CLI_ROOT";

    /// <summary>
    /// The Owner's username, which the Backend requires. The Owner is the one person who signs in to Lookout.
    /// </summary>
    public const string LookoutOwnerUsername = "LOOKOUT_OWNER_USERNAME";

    /// <summary>
    /// The Owner's password, which the Backend requires, at least <see cref="OwnerPasswordMinLength"/> characters.
    /// Changing it, or <see cref="LookoutOwnerUsername"/> other than its case, signs out every session once the Backend restarts.
    /// </summary>
    public const string LookoutOwnerPassword = "LOOKOUT_OWNER_PASSWORD";

    /// <summary>
    /// The shortest <see cref="LookoutOwnerPassword"/> the Backend and the AppHost accept.
    /// </summary>
    public const int OwnerPasswordMinLength = 16;

    /// <summary>
    /// The reverse proxies the Backend trusts the <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> headers from, as
    /// comma-separated IP addresses or CIDR networks, such as <c>172.30.0.1, 10.0.0.0/24</c>. Without it, the Backend trusts
    /// none and ignores both headers.
    /// </summary>
    public const string LookoutTrustedProxies = "LOOKOUT_TRUSTED_PROXIES";

    /// <summary>
    /// Where the Owner opens Lookout, such as <c>https://lookout.example.com</c>, which the Backend links notifications to
    /// their runs with. Without it, notifications have no link.
    /// </summary>
    public const string LookoutPublicUrl = "LOOKOUT_PUBLIC_URL";

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
    /// <see cref="GluetunProxyUrl"/>, with <see cref="GluetunProxyPassword"/> as its password if that's set.
    /// </summary>
    /// <returns>The URL, or <see langword="null"/> if <see cref="GluetunProxyUrl"/> is not set.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="GluetunProxyUrl"/> is not an absolute URL, or has no password, in itself or in <see cref="GluetunProxyPassword"/>:
    /// gluetun's proxy without one lets anyone who reaches it out through the VPN.
    /// </exception>
    public static Uri? GetVpnProxyUrl(IConfiguration configuration)
    {
        if (GetBaseUrl(configuration, GluetunProxyUrl) is not { } url)
        {
            return null;
        }

        if (configuration[GluetunProxyPassword] is not { Length: > 0 } password)
        {
            return url.UserInfo.Split(':', 2) is [_, { Length: > 0 }]
                ? url
                : throw new InvalidOperationException(
                    $"Environment variable {GluetunProxyPassword} must be set to the password of gluetun's HTTP proxy.");
        }

        // UriBuilder doesn't escape the user info, and a password may hold any character
        return new UriBuilder(url) { Password = Uri.EscapeDataString(password) }.Uri;
    }

    /// <summary>
    /// <see cref="BrowserlessUrl"/>, with <see cref="BrowserlessToken"/> as its <c>token</c> query parameter.
    /// </summary>
    /// <returns>The URL, or <see langword="null"/> if <see cref="BrowserlessUrl"/> is not set.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="BrowserlessUrl"/> is not an absolute URL, or <see cref="BrowserlessToken"/> is not set: without a token,
    /// browserless accepts anyone.
    /// </exception>
    public static string? GetBrowserlessUrl(IConfiguration configuration)
    {
        if (GetBaseUrl(configuration, BrowserlessUrl) is not { } url)
        {
            return null;
        }

        if (configuration[BrowserlessToken] is not { Length: > 0 } token)
        {
            throw new InvalidOperationException($"Environment variable {BrowserlessToken} must be set to browserless's token.");
        }

        return new UriBuilder(url) { Query = "token=" + Uri.EscapeDataString(token) }.Uri.AbsoluteUri;
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
