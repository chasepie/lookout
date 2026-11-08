using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm;
using ChasePie.Lookout.Core;

namespace Chasepie.Lookout.AppHost;

// `aspire publish`: every service a container, as the Docker Compose files in deploy/. Nothing here may come from this
// machine (no .env, no paths, no generated values), so the files come out the same wherever they're published, and CI
// checks the committed ones are current. Every setting, secrets included, comes from the deployment's .env.
// deploy/README.md says how to run them, and docs/security.md why they're built this way.
public partial class Program
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

    // For the secrets the Owner chooses: Compose expands $NAME in .env values, and # starts a comment, unless quoted
    private const string QuoteIfNeeded = "Put it in single quotes if it has $, # or spaces in it.";

    private static void AddProduction(IDistributedApplicationBuilder builder)
    {
        var compose = builder.AddDockerComposeEnvironment("lookout")
            // It shows every service's environment, and would need its own sign-in; use `docker compose logs`
            .WithDashboard(false);

        // Settings, which `aspire publish` lists in deploy/.env. Ones with a default fall back to it when left empty, as
        // ${NAME:-default}. Required ones have none, and the service that needs one refuses to start without it.
        var settingDefaults = new Dictionary<string, string>();
        IResourceBuilder<ParameterResource> Setting(string name, string? defaultValue, string description, bool secret = false)
        {
            if (defaultValue is not null)
            {
                settingDefaults[name] = defaultValue;
                description += $" Default: {(defaultValue.Length > 0 ? defaultValue : "none")}.";
            }
            else
            {
                description = "Required: " + description;
            }

            return builder.AddParameter(name, secret: secret).WithDescription(description);
        }

        var imageTag = Setting(
            name: "lookout-image-tag",
            defaultValue: "main",
            description: "The tag of the Backend's and the Runner's images: main, a version such as 1.2.0, or sha-<commit>.");

        var host = Setting(
            name: "lookout-host",
            defaultValue: null,
            description: "the hostname Lookout is served on, such as lookout.example.com. Requests for any other are refused.");

        var bindAddress = Setting(
            name: "lookout-bind-address",
            defaultValue: "127.0.0.1",
            description: "The host address the Backend's port is published on: 127.0.0.1 for a reverse proxy on this machine.");

        var port = Setting(
            name: "lookout-port",
            defaultValue: "8080",
            description: "The host port the Backend is published on.");

        var trustedProxies = Setting(
            name: EnvVars.LookoutTrustedProxies.ToKebabCase(),
            defaultValue: "",
            description: "The reverse proxy's address as the Backend sees it, as comma-separated IPs or CIDR networks. Without it, "
            + "X-Forwarded-For and X-Forwarded-Proto are ignored, and signing in through the proxy fails.");

        var ownerUsername = Setting(
            name: EnvVars.LookoutOwnerUsername.ToKebabCase(),
            defaultValue: null,
            description: "the username the Owner signs in with.");

        var timeZone = Setting(
            name: "tz",
            defaultValue: "Etc/UTC",
            description: "The time zone of schedules and scripts' dates, such as America/New_York.");

        var edgeSubnet = Setting(
            name: "lookout-edge-subnet",
            defaultValue: "172.30.240.0/24",
            description: "The subnet of the Backend's edge network, where its published port's connections come from; its gateway is "
                + "the subnet's first address, 172.30.240.1 by default. Fixed, so the gateway stays the same when the network is "
                + "re-created. Change it if it overlaps another network on this machine.");

        var vpnServiceProvider = Setting(
            name: "gluetun-vpn-service-provider",
            defaultValue: "private internet access",
            description: "gluetun's VPN_SERVICE_PROVIDER.");

        var runnerMemory = Setting(
            name: "lookout-runner-memory",
            defaultValue: "1g",
            description: "The Runner's memory limit, which bounds what a script can take.");

        var browserlessMemory = Setting(
            name: "browserless-memory",
            defaultValue: "2g",
            description: "browserless's memory limit.");

        // Secrets
        IResourceBuilder<ParameterResource> Secret(string name, string description) => Setting(name, null, description, true);

        var ownerPassword = Secret(
            name: EnvVars.LookoutOwnerPassword.ToKebabCase(),
            description: $"the password the Owner signs in with, at least 16 characters. Changing it signs out every session. {QuoteIfNeeded}");

        var runnerApiKey = Secret(
            name: EnvVars.LookoutRunnerApiKey.ToKebabCase(),
            description: "the key the Backend sends the Runner, such as from `openssl rand -hex 32`.");

        var browserlessToken = Secret(
            name: EnvVars.BrowserlessToken.ToKebabCase(),
            description: "browserless's token, such as from `openssl rand -hex 32`.");

        var gluetunApiKey = Secret(
            name: EnvVars.GluetunApiKey.ToKebabCase(),
            description: "the Backend's key for gluetun's control server, letters and digits only, such as from `openssl rand -hex 32`.");

        var proxyPassword = Secret(
            name: EnvVars.GluetunProxyPassword.ToKebabCase(),
            description: "the password of gluetun's HTTP proxy, such as from `openssl rand -hex 32`.");

        var openVpnUser = Secret(
            name: "gluetun-openvpn-user",
            description: $"your VPN provider's OpenVPN username. {QuoteIfNeeded}");

        var openVpnPassword = Secret(
            name: "gluetun-openvpn-password",
            description: $"your VPN provider's OpenVPN password. {QuoteIfNeeded}");

        // The edge network's subnet as a Compose variable, read from the Backend's service for the network
        string? edgeSubnetPlaceholder = null;

        // A setting as a Compose variable, with its default if it has one
        string Placeholder(IResourceBuilder<ParameterResource> setting, DockerComposeServiceResource service) =>
            setting.AsEnvironmentPlaceholder(service) is var placeholder
                && settingDefaults.TryGetValue(setting.Resource.Name, out var defaultValue)
                ? $"{placeholder[..^1]}:-{defaultValue}}}"
                : placeholder;

        var browserless = builder.AddContainer(BrowserlessService, BrowserlessImage, BrowserlessTag)
            .PublishAsDockerComposeService((resource, service) =>
            {
                service.Environment["TZ"] = Placeholder(timeZone, resource);
                service.Environment["TIMEOUT"] = BrowserlessTimeout;
                service.Environment["TOKEN"] = Placeholder(browserlessToken, resource);
                Harden(service, readOnly: false);
                LimitMemory(service, Placeholder(browserlessMemory, resource));
                // Chromium loads whatever pages scripts visit, so it shares no network with the Backend
                service.Networks = [BrowsersNetwork, VpnProxyNetwork, EgressNetwork];
            });

        var runner = builder.AddContainer(RunnerService, RunnerImage)
            .WithVolume(ScriptsVolume, "/app/scripts", isReadOnly: true)
            .WaitFor(browserless)
            .PublishAsDockerComposeService((resource, service) =>
            {
                service.Image = $"{RunnerImage}:{Placeholder(imageTag, resource)}";
                service.Environment["TZ"] = Placeholder(timeZone, resource);
                service.Environment[EnvVars.BrowserlessToken] = Placeholder(browserlessToken, resource);
                service.Environment[EnvVars.BrowserlessUrl] = $"http://{BrowserlessService}:3000";
                service.Environment[EnvVars.GluetunProxyPassword] = Placeholder(proxyPassword, resource);
                service.Environment[EnvVars.GluetunProxyUrl] = $"http://{ProxyUsername}@{GluetunService}:8888";
                service.Environment[EnvVars.LookoutRunnerApiKey] = Placeholder(runnerApiKey, resource);
                Harden(service, readOnly: true);
                // Outside V8's heap, nothing else stops a script taking the host's memory
                LimitMemory(service, Placeholder(runnerMemory, resource));
                // No route out: scripts reach the network only through browserless
                service.Networks = [RunnerNetwork, BrowsersNetwork];
            });

        builder.AddContainer(BackendService, BackendImage)
            .WithVolume(ScriptsVolume, "/app/scripts")
            .WithVolume(DatabaseVolume, "/app/database")
            // For the servers list, which says which regions there are
            .WithVolume(GluetunVolume, "/app/gluetun", isReadOnly: true)
            .WaitFor(runner)
            .PublishAsDockerComposeService((resource, service) =>
            {
                service.Image = $"{BackendImage}:{Placeholder(imageTag, resource)}";
                edgeSubnetPlaceholder = Placeholder(edgeSubnet, resource);

                service.Environment["AllowedHosts"] = Placeholder(host, resource);
                service.Environment["TZ"] = Placeholder(timeZone, resource);
                service.Environment[EnvVars.GluetunApiKey] = Placeholder(gluetunApiKey, resource);
                service.Environment[EnvVars.GluetunDataDir] = "/app/gluetun";
                service.Environment[EnvVars.GluetunProxyPassword] = Placeholder(proxyPassword, resource);
                service.Environment[EnvVars.GluetunProxyUrl] = $"http://{ProxyUsername}@{GluetunProxyRelay}:8888";
                service.Environment[EnvVars.GluetunUrl] = $"http://{GluetunControlRelay}:8000";
                service.Environment[EnvVars.LookoutOwnerPassword] = Placeholder(ownerPassword, resource);
                service.Environment[EnvVars.LookoutOwnerUsername] = Placeholder(ownerUsername, resource);
                service.Environment[EnvVars.LookoutRunnerApiKey] = Placeholder(runnerApiKey, resource);
                service.Environment[EnvVars.LookoutRunnerUrl] = $"http://{RunnerService}:8080";
                service.Environment[EnvVars.LookoutTrustedProxies] = Placeholder(trustedProxies, resource);

                Harden(service, readOnly: true);
                // Plain HTTP, for a reverse proxy that terminates TLS
                service.Ports = [$"{Placeholder(bindAddress, resource)}:{Placeholder(port, resource)}:8080"];
                service.Networks = [RunnerNetwork, VpnControlNetwork, EdgeNetwork];
            });

        // The key as a Compose variable, for gluetun's auth config
        string? gluetunApiKeyPlaceholder = null;
        builder.AddContainer(GluetunService, GluetunImage, GluetunTag)
            .WithVolume(GluetunVolume, "/gluetun")
            .PublishAsDockerComposeService((resource, service) =>
            {
                gluetunApiKeyPlaceholder = Placeholder(gluetunApiKey, resource);
                service.Environment["TZ"] = Placeholder(timeZone, resource);
                service.Environment["VPN_SERVICE_PROVIDER"] = Placeholder(vpnServiceProvider, resource);
                service.Environment["VPN_TYPE"] = "openvpn";
                service.Environment["OPENVPN_USER"] = Placeholder(openVpnUser, resource);
                service.Environment["OPENVPN_PASSWORD"] = Placeholder(openVpnPassword, resource);
                service.Environment["HTTPPROXY"] = "on";
                service.Environment["HTTPPROXY_USER"] = ProxyUsername;
                service.Environment["HTTPPROXY_PASSWORD"] = Placeholder(proxyPassword, resource);
                // Otherwise the proxy adds headers to plain-HTTP requests that give away browserless's address
                service.Environment["HTTPPROXY_STEALTH"] = "on";
                service.Environment["HTTP_CONTROL_SERVER_AUTH_CONFIG_FILEPATH"] = GluetunAuthPath;
                service.Environment["UPDATER_PERIOD"] = GluetunUpdaterPeriod;
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

        AddRelay(builder, GluetunControlRelay, 8000);
        AddRelay(builder, GluetunProxyRelay, 8888);

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
                file.Networks[name] = new Network { Name = name, Driver = "bridge", Internal = true };
            }

            file.Networks[EgressNetwork] = new Network { Name = EgressNetwork, Driver = "bridge" };
            // A fixed subnet, so the gateway that a reverse proxy on this machine connects from, which the Backend is told
            // to trust, stays the same when the network is re-created
            file.Networks[EdgeNetwork] = new Network
            {
                Name = EdgeNetwork,
                Driver = "bridge",
                Ipam = new Ipam
                {
                    Config = [new() { ["subnet"] = edgeSubnetPlaceholder
                        ?? throw new InvalidOperationException("the Backend's service must be published before the compose file") }],
                },
            };

            // Compose fills in the key from deploy/.env
            file.Configs[GluetunAuthConfigName] = new Config
            {
                Name = GluetunAuthConfigName,
                Content = GluetunAuthConfig(gluetunApiKeyPlaceholder
                    ?? throw new InvalidOperationException("gluetun's service must be published before the compose file")),
            };
        });
    }

    /// <summary>
    /// A relay to one of gluetun's ports, for the Backend. A page loaded through gluetun's proxy can reach the relay, but
    /// it leads only back to gluetun.
    /// </summary>
    private static void AddRelay(IDistributedApplicationBuilder builder, string name, int port) =>
        builder.AddContainer(name, AlpineSocatImage, AlpineSocatTag)
            .WithArgs($"TCP-LISTEN:{port},fork,reuseaddr", $"TCP:{GluetunService}:{port}")
            .PublishAsDockerComposeService((_, service) =>
            {
                Harden(service, readOnly: true);
                // nobody
                service.User = "65534:65534";
                service.Networks = [VpnControlNetwork, GluetunRelayNetwork];
            });

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
    private static void LimitMemory(Service service, string memory) =>
        service.Deploy = new Deploy { Resources = new Resources { Limits = new ResourceSpec { Memory = memory } } };
}
