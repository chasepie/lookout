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
        var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
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

        var runner = builder.AddProject<Projects.Runner>("runner")
            .WithEnvironment(LookoutEnvironmentVariables.BrowserlessUrl, browserlessUrl)
            .WithHttpHealthCheck("/health")
            .WaitFor(browserless);

        // VPN client that exposes an HTTP proxy on port 8888, so traffic sent through it leaves over the VPN.
        // Only started with OpenVPN credentials; without it, runs of watches that use the VPN fail.
        if (DotEnvValue("OPENVPN_USER") is { } openVpnUser && DotEnvValue("OPENVPN_PASSWORD") is { } openVpnPasswordValue)
        {
            var openVpnPassword = builder.AddParameter("openvpn-password", openVpnPasswordValue, secret: true);
            var gluetun = builder.AddContainer("gluetun", "qmcgaw/gluetun")
                // Aspire has no API for capabilities or devices, so they're passed to `docker run` directly
                .WithContainerRuntimeArgs("--cap-add=NET_ADMIN", "--device=/dev/net/tun:/dev/net/tun")
                // Keeps the server list it downloads between runs
                .WithVolume("lookout-gluetun", "/gluetun")
                .WithEnvironment("VPN_SERVICE_PROVIDER", DotEnvValue("VPN_SERVICE_PROVIDER") ?? "private internet access")
                .WithEnvironment("VPN_TYPE", "openvpn")
                .WithEnvironment("OPENVPN_USER", openVpnUser)
                .WithEnvironment("OPENVPN_PASSWORD", openVpnPassword)
                .WithEnvironment("HTTPPROXY", "on")
                .WithEnvironment("TZ", timeZone)
                .WithEnvironment("UPDATER_PERIOD", "360h")
                .WithEndpoint(targetPort: 8888, scheme: "http", name: "proxy");

            // The browser in the browserless container connects to the proxy, so the Runner gets its address
            // on the container network rather than the host port. The Runner doesn't wait for gluetun: watches
            // without the VPN don't need it, and a VPN run fails while the proxy is down instead of leaking.
            var proxyOnContainerNetwork = gluetun.Resource.GetEndpoint("proxy", KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
            runner.WithEnvironment(LookoutEnvironmentVariables.VpnProxyUrl, proxyOnContainerNetwork);
        }

        var backend = builder.AddProject<Projects.Backend>("backend")
            .WithEnvironment(LookoutEnvironmentVariables.RunnerUrl, runner.GetEndpoint("https"))
            .WithHttpHealthCheck("/health")
            .WaitFor(runner);

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
        // Backend through proxy.conf.json, which targets the Backend's launch profile port (7070).
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
