using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.AppHost;

public partial class Program
{
    // `aspire publish`: every service a container, as the Docker Compose files in deploy/. Nothing here may come from this
    // machine (no .env, no paths, no generated values), so the files come out the same wherever they're published, and CI
    // checks the committed ones are current. Every setting, secrets included, comes from the deployment's .env.
    // deploy/README.md says how to run them, and docs/security.md why they're built this way.
    private static class Production
    {
        // The services' names, which are also their hostnames on their networks
        private const string BackendService = "backend";
        private const string RunnerService = "runner";
        private const string BrowserlessService = "browserless";
        private const string GluetunService = "gluetun";
        private const string GluetunControlRelay = "gluetun-control";
        private const string GluetunProxyRelay = "gluetun-proxy";

        // One network per pair of services that talk, so each reaches only what it must. Only egress and edge lead out.
        private const string RunnerNetwork = "runner"; // the Backend and the Runner
        private const string BrowsersNetwork = "browsers"; // the Runner and browserless
        private const string VpnProxyNetwork = "vpn-proxy"; // browserless and gluetun's proxy
        private const string GluetunRelayNetwork = "gluetun-relay"; // gluetun and the relays to it
        private const string VpnControlNetwork = "vpn-control"; // the relays and the Backend
        private const string EgressNetwork = "egress"; // browserless and gluetun, out to the internet
        private const string EdgeNetwork = "edge"; // the Backend, reached on its published port
        private static readonly string[] InternalNetworks =
            [RunnerNetwork, BrowsersNetwork, VpnProxyNetwork, GluetunRelayNetwork, VpnControlNetwork];

        private const string ScriptsVolume = "scripts";
        private const string DatabaseVolume = "database";
        private const string GluetunVolume = "gluetun";

        private const string GluetunAuthConfigName = "gluetun_auth";

        public static void Add(IDistributedApplicationBuilder builder)
        {
            var compose = builder
                .AddDockerComposeEnvironment("lookout")
                // It shows every service's environment, and would need its own sign-in; use `docker compose logs`
                .WithDashboard(false);
            var settings = new ProductionSettings(builder);

            var browserless = AddBrowserless(builder, settings);
            var runner = AddRunner(builder, settings, browserless);
            AddBackend(builder, settings, runner);
            AddGluetun(builder, settings);
            AddRelay(builder, GluetunControlRelay, 8000);
            AddRelay(builder, GluetunProxyRelay, 8888);

            ConfigureFiles(compose, settings);
        }

        private static IResourceBuilder<ContainerResource> AddBrowserless(
            IDistributedApplicationBuilder builder,
            ProductionSettings settings)
        {
            return builder
                .AddContainer(BrowserlessService, BrowserlessImage, BrowserlessTag)
                .PublishAsDockerComposeService((resource, service) =>
                {
                    AddEnvironment(service, BrowserlessEnvironment);
                    service.Environment["TZ"] = settings.Placeholder(settings.TimeZone, resource);
                    service.Environment["TOKEN"] = settings.Placeholder(settings.BrowserlessToken, resource);
                    // As many sessions as the Backend starts runs, from the same setting
                    service.Environment[BrowserlessConcurrent] = settings.Placeholder(settings.MaxConcurrentRuns, resource);
                    Harden(service, readOnly: false);
                    LimitMemory(service, settings.Placeholder(settings.BrowserlessMemory, resource));
                    // Chromium loads whatever pages scripts visit, so it shares no network with the Backend
                    service.Networks = [BrowsersNetwork, VpnProxyNetwork, EgressNetwork];
                });
        }

        private static IResourceBuilder<ContainerResource> AddRunner(
            IDistributedApplicationBuilder builder,
            ProductionSettings settings,
            IResourceBuilder<ContainerResource> browserless)
        {
            return builder
                .AddContainer(RunnerService, RunnerImage)
                .WithVolume(ScriptsVolume, "/app/scripts", isReadOnly: true)
                .WaitFor(browserless)
                .PublishAsDockerComposeService((resource, service) =>
                {
                    service.Image = $"{RunnerImage}:{settings.Placeholder(settings.ImageTag, resource)}";
                    service.Environment["TZ"] = settings.Placeholder(settings.TimeZone, resource);
                    service.Environment[EnvVars.BrowserlessToken] = settings.Placeholder(settings.BrowserlessToken, resource);
                    service.Environment[EnvVars.BrowserlessUrl] = $"http://{BrowserlessService}:3000";
                    service.Environment[EnvVars.GluetunProxyPassword] = settings.Placeholder(settings.ProxyPassword, resource);
                    // browserless connects to it, on the network they share
                    service.Environment[EnvVars.GluetunProxyUrl] = $"http://{ProxyUsername}@{GluetunService}:8888";
                    service.Environment[EnvVars.LookoutRunnerApiKey] = settings.Placeholder(settings.RunnerApiKey, resource);
                    Harden(service, readOnly: true);
                    // Outside V8's heap, nothing else stops a script taking the host's memory
                    LimitMemory(service, settings.Placeholder(settings.RunnerMemory, resource));
                    // No route out: scripts reach the network only through browserless
                    service.Networks = [RunnerNetwork, BrowsersNetwork];
                });
        }

        private static void AddBackend(
            IDistributedApplicationBuilder builder,
            ProductionSettings settings,
            IResourceBuilder<ContainerResource> runner)
        {
            builder
                .AddContainer(BackendService, BackendImage)
                .WithVolume(ScriptsVolume, "/app/scripts")
                .WithVolume(DatabaseVolume, "/app/database")
                // For the servers list, which says which regions there are
                .WithVolume(GluetunVolume, "/app/gluetun", isReadOnly: true)
                .WaitFor(runner)
                .PublishAsDockerComposeService((resource, service) =>
                {
                    service.Image = $"{BackendImage}:{settings.Placeholder(settings.ImageTag, resource)}";
                    settings.EdgeSubnetPlaceholder = settings.Placeholder(settings.EdgeSubnet, resource);

                    service.Environment["AllowedHosts"] = settings.Placeholder(settings.Host, resource);
                    service.Environment["TZ"] = settings.Placeholder(settings.TimeZone, resource);
                    service.Environment[EnvVars.GluetunApiKey] = settings.Placeholder(settings.GluetunApiKey, resource);
                    service.Environment[EnvVars.GluetunDataDir] = "/app/gluetun";
                    service.Environment[EnvVars.GluetunProxyPassword] = settings.Placeholder(settings.ProxyPassword, resource);
                    // Through the relay, since the Backend shares no network with gluetun
                    service.Environment[EnvVars.GluetunProxyUrl] = $"http://{ProxyUsername}@{GluetunProxyRelay}:8888";
                    // Each switch replaces gluetun's filter, so the Backend keeps to the same regions
                    service.Environment[EnvVars.GluetunServerRegions] = settings.Placeholder(settings.ServerRegions, resource);
                    service.Environment[EnvVars.GluetunUrl] = $"http://{GluetunControlRelay}:8000";
                    service.Environment[EnvVars.LookoutMaxConcurrentRuns] = settings.Placeholder(settings.MaxConcurrentRuns, resource);
                    service.Environment[EnvVars.LookoutOwnerPassword] = settings.Placeholder(settings.OwnerPassword, resource);
                    service.Environment[EnvVars.LookoutOwnerUsername] = settings.Placeholder(settings.OwnerUsername, resource);
                    // Served over HTTPS on the host it's allowed, so notifications link to their runs there
                    service.Environment[EnvVars.LookoutPublicUrl] = $"https://{settings.Placeholder(settings.Host, resource)}";
                    service.Environment[EnvVars.LookoutRunnerApiKey] = settings.Placeholder(settings.RunnerApiKey, resource);
                    service.Environment[EnvVars.LookoutRunnerUrl] = $"http://{RunnerService}:8080";
                    service.Environment[EnvVars.LookoutTrustedProxies] = settings.Placeholder(settings.TrustedProxies, resource);

                    Harden(service, readOnly: true);
                    // Plain HTTP, for a reverse proxy that terminates TLS
                    service.Ports =
                        [$"{settings.Placeholder(settings.BindAddress, resource)}:{settings.Placeholder(settings.Port, resource)}:8080"];
                    service.Networks = [RunnerNetwork, VpnControlNetwork, EdgeNetwork];
                });
        }

        private static void AddGluetun(IDistributedApplicationBuilder builder, ProductionSettings settings)
        {
            builder
                .AddContainer(GluetunService, GluetunImage, GluetunTag)
                .WithVolume(GluetunVolume, "/gluetun")
                .PublishAsDockerComposeService((resource, service) =>
                {
                    settings.GluetunApiKeyPlaceholder = settings.Placeholder(settings.GluetunApiKey, resource);
                    AddEnvironment(service, GluetunEnvironment);
                    service.Environment["TZ"] = settings.Placeholder(settings.TimeZone, resource);
                    service.Environment["VPN_SERVICE_PROVIDER"] = settings.Placeholder(settings.VpnServiceProvider, resource);
                    service.Environment["SERVER_REGIONS"] = settings.Placeholder(settings.ServerRegions, resource);
                    service.Environment["OPENVPN_USER"] = settings.Placeholder(settings.OpenVpnUser, resource);
                    service.Environment["OPENVPN_PASSWORD"] = settings.Placeholder(settings.OpenVpnPassword, resource);
                    service.Environment["HTTPPROXY_PASSWORD"] = settings.Placeholder(settings.ProxyPassword, resource);
                    service.Restart = "unless-stopped";
                    service.CapAdd = ["NET_ADMIN"];
                    service.Devices = ["/dev/net/tun:/dev/net/tun"];
                    service.AddConfig(new ConfigReference
                    {
                        Source = GluetunAuthConfigName,
                        Target = GluetunAuthPath,
                    });
                    // Its proxy forwards to every network it's on, so the Backend reaches it only through the relays
                    service.Networks = [VpnProxyNetwork, GluetunRelayNetwork, EgressNetwork];
                });
        }

        /// <summary>
        /// A relay to one of gluetun's ports, for the Backend. A page loaded through gluetun's proxy can reach the relay, but
        /// it leads only back to gluetun.
        /// </summary>
        private static void AddRelay(IDistributedApplicationBuilder builder, string name, int port)
        {
            builder
                .AddContainer(name, AlpineSocatImage, AlpineSocatTag)
                .WithArgs($"TCP-LISTEN:{port},fork,reuseaddr", $"TCP:{GluetunService}:{port}")
                .PublishAsDockerComposeService((_, service) =>
                {
                    Harden(service, readOnly: true);
                    // nobody
                    service.User = "65534:65534";
                    service.Networks = [VpnControlNetwork, GluetunRelayNetwork];
                });
        }

        /// <summary>
        /// The settings' descriptions in <c>deploy/.env</c>, and the compose file's name, networks and gluetun's auth config.
        /// </summary>
        private static void ConfigureFiles(IResourceBuilder<DockerComposeEnvironmentResource> compose, ProductionSettings settings)
        {
            compose.ConfigureEnvFile(variables =>
            {
                foreach (var variable in variables.Values)
                {
                    if (variable.Source is ParameterResource parameter)
                    {
                        variable.Description = parameter.Description;
                    }
                }
            });

            compose.ConfigureComposeFile(file =>
            {
                // So volumes and networks are named lookout_*, wherever the file is, rather than after its directory, which a
                // copied or renamed deployment would otherwise lose its data to
                file.Name = "lookout";

                // Each service names its own networks, instead of all sharing Aspire's default one
                file.Networks.Clear();
                foreach (var name in InternalNetworks)
                {
                    file.Networks[name] = new Network
                    {
                        Name = name,
                        Driver = "bridge",
                        Internal = true
                    };
                }

                file.Networks[EgressNetwork] = new Network
                {
                    Name = EgressNetwork,
                    Driver = "bridge"
                };
                // A fixed subnet, so the gateway that a reverse proxy on this machine connects from, which the Backend is told
                // to trust, stays the same when the network is re-created
                file.Networks[EdgeNetwork] = new Network
                {
                    Name = EdgeNetwork,
                    Driver = "bridge",
                    Ipam = new Ipam
                    {
                        Config = [new() { ["subnet"] = settings.EdgeSubnetPlaceholder
                            ?? throw new InvalidOperationException("the Backend's service must be published before the compose file") }],
                    },
                };

                // Compose fills in the key from deploy/.env
                file.Configs[GluetunAuthConfigName] = new Config
                {
                    Name = GluetunAuthConfigName,
                    Content = GluetunAuthConfig(settings.GluetunApiKeyPlaceholder
                        ?? throw new InvalidOperationException("gluetun's service must be published before the compose file")),
                };
            });
        }

        private static void AddEnvironment(Service service, IReadOnlyDictionary<string, string> variables)
        {
            foreach (var (name, value) in variables)
            {
                service.Environment[name] = value;
            }
        }

        /// <summary>
        /// Restarted unless stopped, with no Linux capabilities and no way to gain privileges, and optionally a read-only
        /// root filesystem with a writable <c>/tmp</c>.
        /// </summary>
        private static void Harden(Service service, bool readOnly)
        {
            service.Restart = "unless-stopped";
            service.CapDrop = ["ALL"];
            service.SecurityOpt = ["no-new-privileges:true"];
            if (readOnly)
            {
                service.ReadOnly = true;
                service.Tmpfs = ["/tmp"];
            }
        }

        /// <summary>
        /// A memory limit. Compose applies <c>deploy.resources</c> without Swarm too.
        /// </summary>
        private static void LimitMemory(Service service, string memory)
        {
            service.Deploy = new Deploy
            {
                Resources = new Resources
                {
                    Limits = new ResourceSpec { Memory = memory }
                }
            };
        }
    }
}
