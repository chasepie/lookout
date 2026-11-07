namespace Chasepie.Lookout.AppHost;

// What development and production have in common
public partial class Program
{
    // Chromium that watch scripts run on instead of a locally launched browser. The SDK connects with Playwright, so the
    // image must support its Playwright version: v2.58.3 supports 1.60 to 1.64.
    private const string BrowserlessImage = "ghcr.io/browserless/chromium";
    private const string BrowserlessTag = "v2.58.3";

    // How long browserless lets a session run, in milliseconds
    private const string BrowserlessTimeout = "180000";

    // The VPN client, with an HTTP proxy that watches with UseVpn send their browser traffic through, and a control
    // server the Backend switches regions with. v3 requires a key on the control server.
    private const string GluetunImage = "qmcgaw/gluetun";
    private const string GluetunTag = "v3";

    // How often gluetun refreshes its servers list; more often risks the VPN provider blocking it
    private const string GluetunUpdaterPeriod = "360h";

    // The username for gluetun's HTTP proxy; only the password needs to be secret
    private const string ProxyUsername = "lookout";

    // Where gluetun reads the file that gives the Backend's key its role, outside its data directory so it's never
    // written there
    private const string GluetunAuthDirectory = "/gluetun-auth";
    private const string GluetunAuthFileName = "config.toml";

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
