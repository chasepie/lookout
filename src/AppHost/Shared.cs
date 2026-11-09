namespace ChasePie.Lookout.AppHost;

// What development and production have in common: how browserless and gluetun are configured, apart from the values that
// come from settings or secrets, and the files gluetun is given
public partial class Program
{
    // How long browserless lets a session run, in milliseconds
    private const string BrowserlessTimeout = "180000";

    // How often gluetun refreshes its servers list; more often risks the VPN provider blocking it. The first refresh comes
    // a whole period after gluetun starts, so until then it uses the list in its data directory or, without one, its image's.
    private const string GluetunUpdaterPeriod = "360h";

    // The servers list in gluetun's data directory
    private const string GluetunServersFileName = "servers.json";

    // The username for gluetun's HTTP proxy; only the password needs to be secret
    private const string ProxyUsername = "lookout";

    // Where gluetun reads the file that gives the Backend's key its role, outside its data directory so it's never
    // written there
    private const string GluetunAuthDirectory = "/gluetun-auth";
    private const string GluetunAuthFileName = "config.toml";
    private const string GluetunAuthPath = GluetunAuthDirectory + "/" + GluetunAuthFileName;

    /// <summary>
    /// browserless's environment, other than its time zone and <c>TOKEN</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> BrowserlessEnvironment = new Dictionary<string, string>
    {
        ["TIMEOUT"] = BrowserlessTimeout,
    };

    /// <summary>
    /// gluetun's environment, other than its time zone, <c>VPN_SERVICE_PROVIDER</c>, <c>SERVER_REGIONS</c>, the OpenVPN
    /// credentials and <c>HTTPPROXY_PASSWORD</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> GluetunEnvironment = new Dictionary<string, string>
    {
        ["VPN_TYPE"] = "openvpn",
        ["HTTPPROXY"] = "on",
        ["HTTPPROXY_USER"] = ProxyUsername,
        // Otherwise the proxy adds headers to plain-HTTP requests that give away browserless's address
        ["HTTPPROXY_STEALTH"] = "on",
        ["UPDATER_PERIOD"] = GluetunUpdaterPeriod,
        // The Backend's key may only use the routes it needs
        ["HTTP_CONTROL_SERVER_AUTH_CONFIG_FILEPATH"] = GluetunAuthPath,
    };

    /// <summary>
    /// The routes of gluetun's control server that the Backend uses: reading and changing the VPN's settings, which
    /// switches regions, and the public IP. With no default role, gluetun refuses every other route.
    /// </summary>
    private static readonly string[] GluetunRoutes = ["GET /v1/vpn/settings", "PUT /v1/vpn/settings", "GET /v1/publicip/ip"];

    /// <summary>
    /// gluetun's auth file, giving <paramref name="apiKey"/> only <see cref="GluetunRoutes"/>. The key is letters and
    /// digits, or a Compose variable, so it needs no escaping in TOML.
    /// </summary>
    private static string GluetunAuthConfig(string apiKey) =>
        $"""
        [[roles]]
        name = "lookout-backend"
        routes = [{string.Join(", ", GluetunRoutes.Select(route => $"\"{route}\""))}]
        auth = "apikey"
        apikey = "{apiKey}"

        """;
}

/// <summary>
/// The settings only the AppHost reads, to pass on to containers under their own names: in <c>.env</c> in development,
/// and in <c>deploy/.env</c> in production. The ones a service reads itself are in <see cref="Core.EnvVars"/>.
/// </summary>
internal static class SettingNames
{
    /// <summary>
    /// The time zone of schedules and scripts' dates, passed to every container as <c>TZ</c>, <see cref="DefaultTimeZone"/>
    /// when not set.
    /// </summary>
    public const string TimeZone = "TZ";

    /// <summary>
    /// gluetun's <c>VPN_SERVICE_PROVIDER</c>, <see cref="DefaultVpnServiceProvider"/> when not set.
    /// </summary>
    public const string GluetunVpnServiceProvider = "GLUETUN_VPN_SERVICE_PROVIDER";

    /// <summary>
    /// gluetun's <c>OPENVPN_USER</c>: the VPN provider's OpenVPN username.
    /// </summary>
    public const string GluetunOpenVpnUser = "GLUETUN_OPENVPN_USER";

    /// <summary>
    /// gluetun's <c>OPENVPN_PASSWORD</c>: the VPN provider's OpenVPN password.
    /// </summary>
    public const string GluetunOpenVpnPassword = "GLUETUN_OPENVPN_PASSWORD";

    public const string DefaultTimeZone = "Etc/UTC";

    public const string DefaultVpnServiceProvider = "private internet access";

    /// <summary>
    /// An environment variable's name as an Aspire parameter's, such as <c>lookout-owner-password</c> for
    /// <c>LOOKOUT_OWNER_PASSWORD</c>. Production's parameters become <c>deploy/.env</c>'s variables, so their names must
    /// turn back into the variable's.
    /// </summary>
    public static string ToKebabCase(this string variableName) => variableName.ToLowerInvariant().Replace('_', '-');
}

internal static class ResourceBuilderExtensions
{
    /// <summary>
    /// Sets every variable in <paramref name="variables"/>, as for a container's fixed environment in Shared.cs.
    /// </summary>
    public static IResourceBuilder<T> WithEnvironmentVariables<T>(
        this IResourceBuilder<T> builder,
        IReadOnlyDictionary<string, string> variables)
        where T : IResourceWithEnvironment
    {
        return builder.WithEnvironment(context =>
        {
            foreach (var (name, value) in variables)
            {
                context.EnvironmentVariables[name] = value;
            }
        });
    }
}
