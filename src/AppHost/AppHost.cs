using ChasePie.Lookout.Core;
using dotenv.net;

namespace Chasepie.Lookout.AppHost;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Aspire starts each project from its own directory, where the services can't find the
        // repo-root .env, so forward the values they need from it.
        var repoRoot = RepoRoot.FindRequired(builder.AppHostDirectory);
        var dotEnv = DotEnv.Read(new DotEnvOptions(envFilePaths: [Path.Combine(repoRoot, ".env")]));
        string? DotEnvValue(string name) =>
            dotEnv.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        // A directory from .env, with a relative path resolved against the repo root, where .env lives
        string? DotEnvDirectory(string name) => DotEnvValue(name) is { } dir ? Path.GetFullPath(dir, repoRoot) : null;

        // A directory the AppHost needs, created if it doesn't exist yet
        string RequiredDirectory(string name, string purpose)
        {
            var dir = DotEnvDirectory(name)
                ?? throw new InvalidOperationException($"Set {name} in .env to the directory {purpose}.");
            Directory.CreateDirectory(dir);
            return dir;
        }

        var timeZone = DotEnvValue("TZ") ?? "America/New_York";

        // Chromium that watch scripts run on instead of a locally launched browser. The SDK connects with
        // Playwright, so the image must support its Playwright version: v2.58.3 supports 1.60 to 1.64.
        // Without a token browserless accepts anyone, who could then drive Chromium against the internal network.
        // A new one every time the AppHost starts; only browserless and the Runner have it.
        var browserlessToken = AddGeneratedSecret(builder, "browserless-token");
        var browserless = builder.AddContainer("browserless", "ghcr.io/browserless/chromium", "v2.58.3")
            .WithEnvironment("TZ", timeZone)
            .WithEnvironment("TIMEOUT", "180000")
            .WithEnvironment("TOKEN", browserlessToken)
            .WithHttpEndpoint(targetPort: 3000, name: "http");
        // The Runner reaches it on the container network. The SDK passes the URL's query on to browserless.
        var browserlessOnContainerNetwork = browserless.Resource.GetEndpoint("http", KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
        var browserlessUrl = ReferenceExpression.Create($"{browserlessOnContainerNetwork}?token={browserlessToken}");

        // A new key every time the AppHost starts; the Runner refuses requests without it, and only the Backend has it
        var runnerApiKey = AddGeneratedSecret(builder, "runner-api-key");

        // The Backend saves uploaded scripts there and the Runner runs them from there
        var watchScriptsDir = RequiredDirectory(LookoutEnvironmentVariables.WatchScriptsDir, "watch scripts are kept in");

        // Watch scripts are code, so the Runner runs in a container that can't see the rest of this machine: as the
        // image's non-root user, with the scripts read-only, no .env or database, no Linux capabilities, and no way to
        // gain privileges. Its root filesystem stays writable (though not by its user, outside /tmp and its home), since
        // Aspire copies the development certificate in, which the Runner's telemetry needs. docs/security.md explains.
        var runner = builder.AddDockerfile("runner", repoRoot, "src/Runner/Dockerfile")
            .WithBindMount(watchScriptsDir, "/app/scripts", isReadOnly: true)
            .WithContainerRuntimeArgs("--cap-drop=ALL", "--security-opt=no-new-privileges")
            // Plain HTTP: this hop stays on this machine
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            // Always Development, as the Runner's launch profile was: /health, which the AppHost polls, is only mapped there
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            // Scripts' dates follow the configured time zone, not the container's UTC
            .WithEnvironment("TZ", timeZone)
            .WithEnvironment(LookoutEnvironmentVariables.RunnerApiKey, runnerApiKey)
            .WithEnvironment(LookoutEnvironmentVariables.BrowserlessUrl, browserlessUrl)
            .WithOtlpExporter()
            .WithHttpHealthCheck("/health")
            .WaitFor(browserless);

        // The Owner's sign-in. The Backend checks them too; checking here stops startup before anything else starts.
        if (DotEnvValue(LookoutEnvironmentVariables.OwnerUsername) is not { } ownerUsername
            || DotEnvValue(LookoutEnvironmentVariables.OwnerPassword) is not { } ownerPasswordValue)
        {
            throw new InvalidOperationException(
                $"Set {LookoutEnvironmentVariables.OwnerUsername} and {LookoutEnvironmentVariables.OwnerPassword} "
                + "in .env to the username and password you sign in to Lookout with.");
        }

        if (ownerPasswordValue.Length < LookoutEnvironmentVariables.OwnerPasswordMinLength)
        {
            throw new InvalidOperationException(
                $"{LookoutEnvironmentVariables.OwnerPassword} in .env must be at least "
                + $"{LookoutEnvironmentVariables.OwnerPasswordMinLength} characters.");
        }

        var ownerPassword = builder.AddParameter("owner-password", ownerPasswordValue, secret: true);

        var backend = builder.AddProject<Projects.Backend>("backend")
            .WithEnvironment(LookoutEnvironmentVariables.OwnerUsername, ownerUsername)
            .WithEnvironment(LookoutEnvironmentVariables.OwnerPassword, ownerPassword)
            .WithEnvironment(LookoutEnvironmentVariables.RunnerUrl, runner.GetEndpoint("http"))
            .WithEnvironment(LookoutEnvironmentVariables.WatchScriptsDir, watchScriptsDir)
            .WithEnvironment(LookoutEnvironmentVariables.RunnerApiKey, runnerApiKey)
            .WithHttpHealthCheck("/health")
            .WaitFor(runner);

        // VPN client that exposes an HTTP proxy on port 8888, so traffic sent through it leaves over the VPN,
        // and a control server on port 8000 that the Backend uses to switch regions.
        // Only started with OpenVPN credentials; without it, runs of watches that use the VPN fail.
        if (DotEnvValue("OPENVPN_USER") is { } openVpnUser && DotEnvValue("OPENVPN_PASSWORD") is { } openVpnPasswordValue)
        {
            // gluetun keeps its servers list there, and the Backend reads the list to know which regions exist
            var gluetunDir = RequiredDirectory(LookoutEnvironmentVariables.GluetunDir, "gluetun keeps its data in");

            var openVpnPassword = builder.AddParameter("openvpn-password", openVpnPasswordValue, secret: true);
            // A new key every time the AppHost starts; only gluetun and the Backend need it
            var gluetunApiKey = AddGeneratedSecret(builder, "gluetun-api-key");
            // Without credentials, anyone who can reach the proxy can send traffic through the VPN. A new password
            // every time the AppHost starts; only gluetun, the Runner and the Backend have it.
            var proxyPassword = AddGeneratedSecret(builder, "gluetun-proxy-password");
            var gluetun = builder.AddContainer("gluetun", "qmcgaw/gluetun")
                // Aspire has no API for capabilities or devices, so they're passed to `docker run` directly
                .WithContainerRuntimeArgs("--cap-add=NET_ADMIN", "--device=/dev/net/tun:/dev/net/tun")
                .WithBindMount(gluetunDir, "/gluetun")
                .WithEnvironment("VPN_SERVICE_PROVIDER", DotEnvValue("VPN_SERVICE_PROVIDER") ?? "private internet access")
                .WithEnvironment("VPN_TYPE", "openvpn")
                .WithEnvironment("OPENVPN_USER", openVpnUser)
                .WithEnvironment("OPENVPN_PASSWORD", openVpnPassword)
                .WithEnvironment("HTTPPROXY", "on")
                .WithEnvironment("HTTPPROXY_USER", ProxyUsername)
                .WithEnvironment("HTTPPROXY_PASSWORD", proxyPassword)
                .WithEnvironment("TZ", timeZone)
                .WithEnvironment("UPDATER_PERIOD", "360h")
                // Every control server route needs this key in the X-API-Key header
                .WithEnvironment(
                    "HTTP_CONTROL_SERVER_AUTH_DEFAULT_ROLE",
                    ReferenceExpression.Create($"{{\"auth\":\"apikey\",\"apikey\":\"{gluetunApiKey}\"}}"))
                .WithEndpoint(targetPort: 8888, scheme: "http", name: "proxy")
                .WithHttpEndpoint(targetPort: 8000, name: "control");

            // The browser in the browserless container connects to the proxy, so the Runner gets its address
            // on the container network rather than the host port. The Runner doesn't wait for gluetun: watches
            // without the VPN don't need it, and a VPN run fails while the proxy is down instead of leaking.
            var proxyOnContainerNetwork = gluetun.Resource.GetEndpoint("proxy", KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
            runner.WithEnvironment(LookoutEnvironmentVariables.VpnProxyUrl, ProxyUrl(proxyOnContainerNetwork, proxyPassword));

            // The Backend doesn't wait for gluetun either: it retries, and shows gluetun as unreachable meanwhile
            backend
                .WithEnvironment(LookoutEnvironmentVariables.GluetunUrl, gluetun.GetEndpoint("control"))
                .WithEnvironment(LookoutEnvironmentVariables.GluetunApiKey, gluetunApiKey)
                .WithEnvironment(LookoutEnvironmentVariables.GluetunDir, gluetunDir)
                // Unlike the Runner's, on the host, since the Backend connects to it itself
                .WithEnvironment(LookoutEnvironmentVariables.VpnProxyUrl, ProxyUrl(gluetun.GetEndpoint("proxy"), proxyPassword));
        }

        if (DotEnvDirectory(LookoutEnvironmentVariables.DatabaseDir) is { } databaseDir)
        {
            backend.WithEnvironment(LookoutEnvironmentVariables.DatabaseDir, databaseDir);
        }

        // Runs `npm run start` (ng serve) on its default port. The dev server forwards /api to the
        // Backend through proxy.conf.json, which targets the Backend's launch profile port (7070). Serving both from
        // one origin is what lets the session cookie and the CSRF checks work.
        // Packages come from the root npm workspace, so run `npm install` at the repo root first.
        var frontend = builder.AddJavaScriptApp("frontend", "../frontend", "start")
            .WithNpm(install: false)
            .WithHttpEndpoint(port: 4200, isProxied: false)
            .WaitFor(backend);

        // Opens Chrome with the debugger attached when the AppHost is launched from VS Code, so
        // breakpoints in the Angular code are hit. The API is still marked experimental.
#pragma warning disable ASPIREEXTENSION001
        frontend.WithBrowserDebugger("chrome");
#pragma warning restore ASPIREEXTENSION001

        builder.Build().Run();
    }

    // The username for gluetun's HTTP proxy; only the password needs to be secret
    private const string ProxyUsername = "lookout";

    // The length of every generated secret, unless the Runner API key's minimum, which the Backend and the Runner
    // check, is longer
    private const int GeneratedSecretLength = 32;

    /// <summary>
    /// A secret generated every time the AppHost starts. Letters and digits only, so it can go in URLs and JSON strings
    /// without escaping.
    /// </summary>
    private static IResourceBuilder<ParameterResource> AddGeneratedSecret(IDistributedApplicationBuilder builder, string name) =>
        builder.AddParameter(
            name,
            new GenerateParameterDefault
            {
                MinLength = Math.Max(GeneratedSecretLength, LookoutEnvironmentVariables.RunnerApiKeyMinLength),
                Special = false,
            },
            secret: true);

    /// <summary>
    /// The URL of gluetun's HTTP proxy at <paramref name="endpoint"/>, with its credentials.
    /// </summary>
    private static ReferenceExpression ProxyUrl(EndpointReference endpoint, IResourceBuilder<ParameterResource> password) =>
        ReferenceExpression.Create($"http://{ProxyUsername}:{password}@{endpoint.Property(EndpointProperty.HostAndPort)}");
}
