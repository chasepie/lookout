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

        var timeZone = DotEnvValue("TZ") ?? "America/New_York";

        // Chromium that watch scripts run on instead of a locally launched browser. The SDK connects with
        // Playwright, so the image must support its Playwright version: v2.58.3 supports 1.60 to 1.64.
        var browserless = builder.AddContainer("browserless", "ghcr.io/browserless/chromium", "v2.58.3")
            .WithEnvironment("TZ", timeZone)
            .WithEnvironment("TIMEOUT", "180000")
            .WithHttpEndpoint(targetPort: 3000, name: "http");
        var browserlessUrl = ReferenceExpression.Create($"{browserless.GetEndpoint("http")}");

        // Without a token browserless accepts unauthenticated connections
        if (DotEnvValue("BROWSERLESS_TOKEN") is { } browserlessToken)
        {
            var token = builder.AddParameter("browserless-token", browserlessToken, secret: true);
            browserless.WithEnvironment("TOKEN", token);
            // The SDK passes the URL's query on to browserless
            browserlessUrl = ReferenceExpression.Create($"{browserless.GetEndpoint("http")}?token={token}");
        }

        // A new key every time the AppHost starts; the Runner refuses requests without it, and only the Backend has it
        var runnerApiKey = builder.AddParameter(
            "runner-api-key",
            new GenerateParameterDefault { MinLength = LookoutEnvironmentVariables.RunnerApiKeyMinLength, Special = false },
            secret: true);

        var runner = builder.AddProject<Projects.Runner>("runner")
            .WithEnvironment(LookoutEnvironmentVariables.RunnerApiKey, runnerApiKey)
            .WithEnvironment(LookoutEnvironmentVariables.BrowserlessUrl, browserlessUrl)
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
            .WithEnvironment(LookoutEnvironmentVariables.RunnerUrl, runner.GetEndpoint("https"))
            .WithEnvironment(LookoutEnvironmentVariables.RunnerApiKey, runnerApiKey)
            .WithHttpHealthCheck("/health")
            .WaitFor(runner);

        // VPN client that exposes an HTTP proxy on port 8888, so traffic sent through it leaves over the VPN,
        // and a control server on port 8000 that the Backend uses to switch regions.
        // Only started with OpenVPN credentials; without it, runs of watches that use the VPN fail.
        if (DotEnvValue("OPENVPN_USER") is { } openVpnUser && DotEnvValue("OPENVPN_PASSWORD") is { } openVpnPasswordValue)
        {
            // gluetun keeps its servers list there, and the Backend reads the list to know which regions exist
            var gluetunDir = DotEnvValue(LookoutEnvironmentVariables.GluetunDir) is { } dir
                ? Path.GetFullPath(dir, repoRoot)
                : throw new InvalidOperationException(
                    $"Set {LookoutEnvironmentVariables.GluetunDir} in .env to the directory gluetun keeps its data in.");
            Directory.CreateDirectory(gluetunDir);

            var openVpnPassword = builder.AddParameter("openvpn-password", openVpnPasswordValue, secret: true);
            // A new key every time the AppHost starts; only gluetun and the Backend need it. Letters and digits
            // only, since it's embedded in a JSON string below.
            var gluetunApiKey = builder.AddParameter(
                "gluetun-api-key", new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true);
            var gluetun = builder.AddContainer("gluetun", "qmcgaw/gluetun")
                // Aspire has no API for capabilities or devices, so they're passed to `docker run` directly
                .WithContainerRuntimeArgs("--cap-add=NET_ADMIN", "--device=/dev/net/tun:/dev/net/tun")
                .WithBindMount(gluetunDir, "/gluetun")
                .WithEnvironment("VPN_SERVICE_PROVIDER", DotEnvValue("VPN_SERVICE_PROVIDER") ?? "private internet access")
                .WithEnvironment("VPN_TYPE", "openvpn")
                .WithEnvironment("OPENVPN_USER", openVpnUser)
                .WithEnvironment("OPENVPN_PASSWORD", openVpnPassword)
                .WithEnvironment("HTTPPROXY", "on")
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
            runner.WithEnvironment(LookoutEnvironmentVariables.VpnProxyUrl, proxyOnContainerNetwork);

            // The Backend doesn't wait for gluetun either: it retries, and shows gluetun as unreachable meanwhile
            backend
                .WithEnvironment(LookoutEnvironmentVariables.GluetunUrl, gluetun.GetEndpoint("control"))
                .WithEnvironment(LookoutEnvironmentVariables.GluetunApiKey, gluetunApiKey)
                .WithEnvironment(LookoutEnvironmentVariables.GluetunDir, gluetunDir)
                // Unlike the Runner's, on the host, since the Backend connects to it itself
                .WithEnvironment(LookoutEnvironmentVariables.VpnProxyUrl, gluetun.GetEndpoint("proxy"));
        }

        // Directories, with a relative path resolved against the repo root, where .env lives
        void ForwardDirectory(string name, params IResourceBuilder<ProjectResource>[] projects)
        {
            if (dotEnv.TryGetValue(name, out var dir) && !string.IsNullOrWhiteSpace(dir))
            {
                var fullPath = Path.GetFullPath(dir, repoRoot);
                foreach (var project in projects)
                {
                    project.WithEnvironment(name, fullPath);
                }
            }
        }

        // The Backend saves uploaded scripts there and the Runner runs them from there
        ForwardDirectory(LookoutEnvironmentVariables.WatchScriptsDir, backend, runner);
        ForwardDirectory(LookoutEnvironmentVariables.DatabaseDir, backend);

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
}
