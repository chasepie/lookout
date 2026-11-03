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

        // Headless Chromium that watch scripts can connect to instead of launching a local browser
        var browserless = builder.AddContainer("browserless", "ghcr.io/browserless/chromium")
            .WithEnvironment("TZ", timeZone)
            .WithEnvironment("TIMEOUT", "180000")
            .WithHttpEndpoint(targetPort: 3000, name: "http");

        // Without a token browserless accepts unauthenticated connections
        if (DotEnvValue("BROWSERLESS_TOKEN") is { } browserlessToken)
        {
            var token = builder.AddParameter("browserless-token", browserlessToken, secret: true);
            browserless.WithEnvironment("TOKEN", token);
        }

        // VPN client that exposes an HTTP proxy on port 8888, so traffic sent through it leaves over the VPN.
        // Aspire has no API for capabilities or devices, so they're passed to `docker run` directly.
        var openVpnPassword = builder.AddParameter("openvpn-password", DotEnvValue("OPENVPN_PASSWORD") ?? "", secret: true);
        builder.AddContainer("gluetun", "qmcgaw/gluetun")
            .WithContainerRuntimeArgs("--cap-add=NET_ADMIN", "--device=/dev/net/tun:/dev/net/tun")
            // Keeps the server list it downloads between runs
            .WithVolume("lookout-gluetun", "/gluetun")
            .WithEnvironment("VPN_SERVICE_PROVIDER", DotEnvValue("VPN_SERVICE_PROVIDER") ?? "private internet access")
            .WithEnvironment("VPN_TYPE", "openvpn")
            .WithEnvironment("OPENVPN_USER", DotEnvValue("OPENVPN_USER") ?? "")
            .WithEnvironment("OPENVPN_PASSWORD", openVpnPassword)
            .WithEnvironment("HTTPPROXY", "on")
            .WithEnvironment("TZ", timeZone)
            .WithEnvironment("UPDATER_PERIOD", "360h")
            .WithEndpoint(targetPort: 8888, scheme: "http", name: "proxy");

        var runner = builder.AddProject<Projects.Runner>("runner")
            .WithEnvironment(LookoutEnvironmentVariables.BrowserlessUrl, browserless.GetEndpoint("http"))
            .WithHttpHealthCheck("/health")
            .WaitFor(browserless);

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
