using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.AppHost;

public partial class Program
{
    // `dotnet run`: the Backend and the frontend on this machine, the rest in containers
    private static class Development
    {
        // The length of every generated secret, unless the Runner API key's minimum, which the Backend and the Runner
        // check, is longer
        private const int GeneratedSecretLength = 32;

        public static void Add(IDistributedApplicationBuilder builder)
        {
            var settings = DevelopmentSettings.Read(RepoRoot.FindRequired(builder.AppHostDirectory));

            // Without a token browserless accepts anyone, who could then drive Chromium against the internal network.
            // A new one every time the AppHost starts; only browserless and the Runner have it.
            var browserlessToken = AddGeneratedSecret(builder, EnvVars.BrowserlessToken);

            // A new key every time the AppHost starts; the Runner refuses requests without it, and only the Backend has it
            var runnerApiKey = AddGeneratedSecret(builder, EnvVars.LookoutRunnerApiKey);
            var watchScriptsDir = settings.WatchScriptsDir();

            var browserless = AddBrowserless(builder, settings, browserlessToken);
            var runner = AddRunner(builder, settings, watchScriptsDir, browserless, browserlessToken, runnerApiKey);
            var backend = AddBackend(builder, settings, watchScriptsDir, runner, runnerApiKey);
            if (settings.OpenVpn is { } openVpn)
            {
                AddGluetun(builder, settings, openVpn, browserless, runner, backend);
            }

            AddFrontend(builder, backend);
        }

        private static IResourceBuilder<ContainerResource> AddBrowserless(
            IDistributedApplicationBuilder builder,
            DevelopmentSettings settings,
            IResourceBuilder<ParameterResource> token)
        {
            return builder
                .AddContainer("browserless", BrowserlessImage, BrowserlessTag)
                .WithEnvironmentVariables(BrowserlessEnvironment)
                .WithEnvironment("TZ", settings.TimeZone)
                .WithEnvironment("TOKEN", token)
                .WithHttpEndpoint(targetPort: 3000, name: "http");
        }

        // Watch scripts are code, so the Runner runs in a container that can't see the rest of this machine: as the
        // image's non-root user, with the scripts read-only, no .env or database, no Linux capabilities, and no way to
        // gain privileges. Its root filesystem stays writable (though not by its user, outside /tmp and its home), since
        // Aspire copies the development certificate in, which the Runner's telemetry needs. Its network isn't limited
        // either, since that needs the Backend in a container too; production does both. docs/security.md explains.
        private static IResourceBuilder<ContainerResource> AddRunner(
            IDistributedApplicationBuilder builder,
            DevelopmentSettings settings,
            string watchScriptsDir,
            IResourceBuilder<ContainerResource> browserless,
            IResourceBuilder<ParameterResource> browserlessToken,
            IResourceBuilder<ParameterResource> runnerApiKey)
        {
            return builder
                .AddDockerfile("runner", settings.RepoRoot, "src/Runner/Dockerfile")
                .WithBindMount(watchScriptsDir, "/app/scripts", isReadOnly: true)
                .WithContainerRuntimeArgs("--cap-drop=ALL", "--security-opt=no-new-privileges")
                // Plain HTTP: this hop stays on this machine
                .WithHttpEndpoint(targetPort: 8080, name: "http")
                // Always Development, as the Runner's launch profile was: /health, which the AppHost polls, is only mapped there
                .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                // Scripts' dates follow the configured time zone, not the container's UTC
                .WithEnvironment("TZ", settings.TimeZone)
                .WithEnvironment(EnvVars.LookoutRunnerApiKey, runnerApiKey)
                // On the container network
                .WithEnvironment(
                    EnvVars.BrowserlessUrl,
                    browserless.Resource.GetEndpoint("http", KnownNetworkIdentifiers.DefaultAspireContainerNetwork))
                .WithEnvironment(EnvVars.BrowserlessToken, browserlessToken)
                .WithOtlpExporter()
                .WithHttpHealthCheck("/health")
                .WaitFor(browserless);
        }

        private static IResourceBuilder<ProjectResource> AddBackend(
            IDistributedApplicationBuilder builder,
            DevelopmentSettings settings,
            string watchScriptsDir,
            IResourceBuilder<ContainerResource> runner,
            IResourceBuilder<ParameterResource> runnerApiKey)
        {
            var owner = settings.RequireOwner();
            var ownerPassword = builder.AddParameter(
                name: EnvVars.LookoutOwnerPassword.ToKebabCase(),
                value: owner.Password,
                secret: true);

            return builder
                .AddProject<Projects.Backend>("backend")
                .WithEnvironment(EnvVars.LookoutOwnerUsername, owner.Username)
                .WithEnvironment(EnvVars.LookoutOwnerPassword, ownerPassword)
                .WithEnvironment(EnvVars.LookoutRunnerUrl, runner.GetEndpoint("http"))
                .WithEnvironment(EnvVars.LookoutWatchScriptsDir, watchScriptsDir)
                .WithEnvironment(EnvVars.LookoutDatabaseDir, settings.DatabaseDir())
                .WithEnvironment(EnvVars.LookoutRunnerApiKey, runnerApiKey)
                .WithHttpHealthCheck("/health")
                .WaitFor(runner);
        }

        /// <summary>
        /// gluetun, with the updater before it when it has no servers list, and its proxy and control server given to the
        /// Runner and the Backend. browserless and the Backend wait for it.
        /// </summary>
        private static void AddGluetun(
            IDistributedApplicationBuilder builder,
            DevelopmentSettings settings,
            OpenVpnCredentials openVpn,
            IResourceBuilder<ContainerResource> browserless,
            IResourceBuilder<ContainerResource> runner,
            IResourceBuilder<ProjectResource> backend)
        {
            var dataDir = settings.GluetunDataDir();

            var openVpnPassword = builder.AddParameter(
                name: SettingNames.GluetunOpenVpnPassword.ToKebabCase(),
                value: openVpn.Password,
                secret: true);

            // A new key every time the AppHost starts; only gluetun and the Backend need it
            var apiKey = AddGeneratedSecret(builder, EnvVars.GluetunApiKey);
            // Without credentials, anyone who can reach the proxy can send traffic through the VPN. A new password
            // every time the AppHost starts; only gluetun, the Runner and the Backend have it.
            var proxyPassword = AddGeneratedSecret(builder, EnvVars.GluetunProxyPassword);
            var gluetun = builder
                .AddContainer("gluetun", GluetunImage, GluetunTag)
                // Aspire has no API for capabilities or devices, so they're passed to `docker run` directly
                .WithContainerRuntimeArgs("--cap-add=NET_ADMIN", "--device=/dev/net/tun:/dev/net/tun")
                .WithBindMount(dataDir, "/gluetun")
                .WithEnvironmentVariables(GluetunEnvironment)
                .WithEnvironment("TZ", settings.TimeZone)
                .WithEnvironment("VPN_SERVICE_PROVIDER", settings.VpnServiceProvider)
                .WithEnvironment("OPENVPN_USER", openVpn.User)
                .WithEnvironment("OPENVPN_PASSWORD", openVpnPassword)
                .WithEnvironment("HTTPPROXY_PASSWORD", proxyPassword)
                .WithContainerFiles(GluetunAuthDirectory, async (_, cancellationToken) =>
                [
                    new ContainerFile
                    {
                        Name = GluetunAuthFileName,
                        Contents = GluetunAuthConfig(await apiKey.Resource.GetValueAsync(cancellationToken) ?? ""),
                    },
                ])
                .WithEndpoint(targetPort: 8888, scheme: "http", name: "proxy")
                .WithHttpEndpoint(targetPort: 8000, name: "control");

            if (settings.GluetunServerRegions is { } serverRegions)
            {
                gluetun.WithEnvironment("SERVER_REGIONS", serverRegions);
                // Each switch replaces gluetun's filter, so the Backend keeps to the same regions
                backend.WithEnvironment(EnvVars.GluetunServerRegions, serverRegions);
            }

            // gluetun refreshes its servers list only after UPDATER_PERIOD without a restart, which in development never
            // comes, so without a list it would use the one built into its image, which may be months out of date and
            // fail to connect. `update` writes that list before fetching the new one, so a failed update deletes it,
            // letting the next start try again; gluetun doesn't start until then.
            if (!File.Exists(Path.Join(dataDir, GluetunServersFileName)))
            {
                var updater = builder
                    .AddContainer("gluetun-updater", GluetunImage, GluetunTag)
                    .WithBindMount(dataDir, "/gluetun")
                    .WithEntrypoint("/bin/sh")
                    // The provider is passed as $1 rather than spliced into the script, so it needs no quoting
                    .WithArgs(
                        "-c",
                        $"/gluetun-entrypoint update -enduser -providers \"$1\" || {{ rm -f /gluetun/{GluetunServersFileName}; exit 1; }}",
                        "gluetun-updater",
                        settings.VpnServiceProvider);
                gluetun.WaitForCompletion(updater);
            }

            // Started after gluetun, which waits for the updater, so neither talks to a gluetun that isn't there yet. gluetun
            // has no health check, so they wait for its container to run, not for its tunnel: a VPN run fails while the
            // tunnel is down instead of leaking, and the Backend retries, showing the VPN as unreachable meanwhile. The
            // Runner waits for browserless, so for gluetun too.
            browserless.WaitFor(gluetun);
            backend.WaitFor(gluetun);

            // The browser in the browserless container connects to the proxy, so the Runner gets its address
            // on the container network rather than the host port
            var proxyOnContainerNetwork = gluetun.Resource.GetEndpoint("proxy", KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
            runner
                .WithEnvironment(EnvVars.GluetunProxyUrl, ProxyUrl(proxyOnContainerNetwork))
                .WithEnvironment(EnvVars.GluetunProxyPassword, proxyPassword);

            backend
                .WithEnvironment(EnvVars.GluetunUrl, gluetun.GetEndpoint("control"))
                .WithEnvironment(EnvVars.GluetunApiKey, apiKey)
                .WithEnvironment(EnvVars.GluetunDataDir, dataDir)
                // Unlike the Runner's, on the host, since the Backend connects to it itself
                .WithEnvironment(EnvVars.GluetunProxyUrl, ProxyUrl(gluetun.GetEndpoint("proxy")))
                .WithEnvironment(EnvVars.GluetunProxyPassword, proxyPassword);
        }

        // Runs `npm run start` (ng serve) on its default port. The dev server forwards /api to the
        // Backend through proxy.conf.mjs, which targets the Backend's launch profile port (7070). Serving both from
        // one origin is what lets the session cookie and the CSRF checks work.
        // The AppHost runs `npm install` before starting it; the packages come from the root npm workspace.
        private static void AddFrontend(IDistributedApplicationBuilder builder, IResourceBuilder<ProjectResource> backend)
        {
            var frontend = builder
                .AddJavaScriptApp("frontend", "../frontend", "start")
                .WithNpm(install: true)
                .WithHttpEndpoint(port: 4200, isProxied: false)
                .WaitFor(backend);

            // Opens Chrome with the debugger attached when the AppHost is launched from VS Code, so
            // breakpoints in the Angular code are hit. The API is still marked experimental.
#pragma warning disable ASPIREEXTENSION001
            frontend.WithBrowserDebugger("chrome");
#pragma warning restore ASPIREEXTENSION001
        }

        /// <summary>
        /// A secret generated every time the AppHost starts, named after the variable it's passed as. Letters and digits
        /// only, so it can go in URLs and TOML strings without escaping.
        /// </summary>
        private static IResourceBuilder<ParameterResource> AddGeneratedSecret(IDistributedApplicationBuilder builder, string variableName)
        {
            return builder.AddParameter(
                name: variableName.ToKebabCase(),
                value: new GenerateParameterDefault
                {
                    MinLength = Math.Max(GeneratedSecretLength, EnvVars.RunnerApiKeyMinLength),
                    Special = false,
                },
                secret: true);
        }

        /// <summary>
        /// The URL of gluetun's HTTP proxy at <paramref name="endpoint"/>, with its username; the password is set on its own.
        /// </summary>
        private static ReferenceExpression ProxyUrl(EndpointReference endpoint) =>
            ReferenceExpression.Create($"http://{ProxyUsername}@{endpoint.Property(EndpointProperty.HostAndPort)}");
    }
}
