using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm;
using ChasePie.Lookout.Core;

namespace Chasepie.Lookout.AppHost;

// `aspire publish`: every service a container, as the Docker Compose files in deploy/. Nothing here may come from this
// machine (no .env, no paths, no generated values), so the files come out the same wherever they're published, and CI
// checks the committed ones are current. deploy/README.md says how to run them, and docs/security.md why they're built
// this way.
public partial class Program
{
    // The images CI pushes
    private const string ImageRegistry = "ghcr.io/chasepie";

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
    private static readonly string[] OutwardNetworks = [EgressNetwork, EdgeNetwork];

    private const string ScriptsVolume = "scripts";
    private const string DatabaseVolume = "database";
    private const string GluetunVolume = "gluetun";

    // Files in deploy/secrets. Each is mounted where its reader looks: a Lookout service reads a setting from the file
    // named after it (see SecretFiles), and gluetun reads its own under fixed names.
    private const string OwnerPasswordSecret = "owner_password";
    private const string RunnerApiKeySecret = "runner_api_key";
    private const string VpnProxyPasswordSecret = "vpn_proxy_password";
    private const string OpenVpnUserSecret = "openvpn_user";
    private const string OpenVpnPasswordSecret = "openvpn_password";
    private static readonly string[] SecretFileNames =
        [OwnerPasswordSecret, RunnerApiKeySecret, VpnProxyPasswordSecret, OpenVpnUserSecret, OpenVpnPasswordSecret];

    private const string GluetunAuthConfigName = "gluetun_auth";

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
                description += defaultValue.Length > 0 ? $" Default: {defaultValue}." : " Default: none.";
            }
            else
            {
                description = "Required: " + description;
            }

            return builder.AddParameter(name, secret: secret).WithDescription(description);
        }

        var imageTag = Setting("lookout-image-tag", "main",
            "The tag of the Backend's and the Runner's images: main, a version such as 1.2.0, or sha-<commit>.");
        var host = Setting("lookout-host", null,
            "the hostname Lookout is served on, such as lookout.example.com. Requests for any other are refused.");
        var bindAddress = Setting("lookout-bind-address", "127.0.0.1",
            "The host address the Backend's port is published on: 127.0.0.1 for a reverse proxy on this machine.");
        var port = Setting("lookout-port", "8080", "The host port the Backend is published on.");
        var trustedProxies = Setting("lookout-trusted-proxies", "",
            "The reverse proxy's address as the Backend sees it, as comma-separated IPs or CIDR networks. Without it, "
            + "X-Forwarded-For and X-Forwarded-Proto are ignored, and signing in through the proxy fails.");
        var ownerUsername = Setting("lookout-owner-username", null,
            "the username the Owner signs in with. The password is in secrets/owner_password.");
        var timeZone = Setting("tz", "Etc/UTC", "The time zone of schedules and scripts' dates, such as America/New_York.");
        var vpnServiceProvider = Setting("vpn-service-provider", "private internet access", "gluetun's VPN_SERVICE_PROVIDER.");
        var runnerMemory = Setting("lookout-runner-memory", "1g", "The Runner's memory limit, which bounds what a script can take.");
        var browserlessMemory = Setting("lookout-browserless-memory", "2g", "browserless's memory limit.");
        // These two go to containers that only read them from their environment or a config, so they're kept here rather
        // than in secrets/
        var browserlessToken = Setting("browserless-token", null,
            "browserless's token, letters and digits only, such as from `openssl rand -hex 32`.", secret: true);
        var gluetunApiKey = Setting("gluetun-api-key", null,
            "the Backend's key for gluetun's control server, letters and digits only, such as from "
            + "`openssl rand -hex 32`.", secret: true);

        // A setting as a Compose variable, with its default if it has one
        string Placeholder(IResourceBuilder<ParameterResource> setting, DockerComposeServiceResource service) =>
            setting.AsEnvironmentPlaceholder(service) is var placeholder
                && settingDefaults.TryGetValue(setting.Resource.Name, out var defaultValue)
                ? $"{placeholder[..^1]}:-{defaultValue}}}"
                : placeholder;

        builder.AddContainer(BrowserlessService, BrowserlessImage, BrowserlessTag)
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

        builder.AddContainer(RunnerService, $"{ImageRegistry}/lookout-runner")
            .PublishAsDockerComposeService((resource, service) =>
            {
                service.Image = $"{ImageRegistry}/lookout-runner:{Placeholder(imageTag, resource)}";
                service.Environment["TZ"] = Placeholder(timeZone, resource);
                service.Environment[LookoutEnvironmentVariables.BrowserlessUrl] = $"http://{BrowserlessService}:3000";
                service.Environment[LookoutEnvironmentVariables.BrowserlessToken] = Placeholder(browserlessToken, resource);
                // browserless connects to it, on the network they share
                service.Environment[LookoutEnvironmentVariables.VpnProxyUrl] = $"http://{ProxyUsername}@{GluetunService}:8888";
                Harden(service, readOnly: true);
                RunUnderInit(service);
                // Outside V8's heap, nothing else stops a script taking the host's memory
                LimitMemory(service, Placeholder(runnerMemory, resource));
                AddVolume(service, ScriptsVolume, "/app/scripts", readOnly: true);
                AddSecret(service, RunnerApiKeySecret, LookoutEnvironmentVariables.RunnerApiKey);
                AddSecret(service, VpnProxyPasswordSecret, LookoutEnvironmentVariables.VpnProxyPassword);
                // No route out: scripts reach the network only through browserless
                service.Networks = [RunnerNetwork, BrowsersNetwork];
                DependOn(service, BrowserlessService);
            });

        builder.AddContainer(BackendService, $"{ImageRegistry}/lookout-backend")
            .PublishAsDockerComposeService((resource, service) =>
            {
                service.Image = $"{ImageRegistry}/lookout-backend:{Placeholder(imageTag, resource)}";
                service.Environment["TZ"] = Placeholder(timeZone, resource);
                service.Environment["AllowedHosts"] = Placeholder(host, resource);
                service.Environment[LookoutEnvironmentVariables.TrustedProxies] = Placeholder(trustedProxies, resource);
                service.Environment[LookoutEnvironmentVariables.OwnerUsername] = Placeholder(ownerUsername, resource);
                service.Environment[LookoutEnvironmentVariables.RunnerUrl] = $"http://{RunnerService}:8080";
                service.Environment[LookoutEnvironmentVariables.GluetunUrl] = $"http://{GluetunControlRelay}:8000";
                service.Environment[LookoutEnvironmentVariables.GluetunApiKey] = Placeholder(gluetunApiKey, resource);
                service.Environment[LookoutEnvironmentVariables.GluetunDir] = "/app/gluetun";
                // Through the relay, since the Backend shares no network with gluetun
                service.Environment[LookoutEnvironmentVariables.VpnProxyUrl] = $"http://{ProxyUsername}@{GluetunProxyRelay}:8888";
                Harden(service, readOnly: true);
                RunUnderInit(service);
                // Plain HTTP, for a reverse proxy that terminates TLS
                service.Ports = [$"{Placeholder(bindAddress, resource)}:{Placeholder(port, resource)}:8080"];
                AddVolume(service, ScriptsVolume, "/app/scripts");
                AddVolume(service, DatabaseVolume, "/app/database");
                // For the servers list, which says which regions there are
                AddVolume(service, GluetunVolume, "/app/gluetun", readOnly: true);
                AddSecret(service, OwnerPasswordSecret, LookoutEnvironmentVariables.OwnerPassword);
                AddSecret(service, RunnerApiKeySecret, LookoutEnvironmentVariables.RunnerApiKey);
                AddSecret(service, VpnProxyPasswordSecret, LookoutEnvironmentVariables.VpnProxyPassword);
                service.Networks = [RunnerNetwork, VpnControlNetwork, EdgeNetwork];
                DependOn(service, RunnerService);
            });

        // The key as a Compose variable, for gluetun's auth config
        string? gluetunApiKeyPlaceholder = null;
        builder.AddContainer(GluetunService, GluetunImage, GluetunTag)
            .PublishAsDockerComposeService((resource, service) =>
            {
                gluetunApiKeyPlaceholder = Placeholder(gluetunApiKey, resource);
                service.Environment["VPN_SERVICE_PROVIDER"] = Placeholder(vpnServiceProvider, resource);
                service.Environment["VPN_TYPE"] = "openvpn";
                service.Environment["HTTPPROXY"] = "on";
                service.Environment["HTTPPROXY_USER"] = ProxyUsername;
                // Otherwise the proxy adds headers to plain-HTTP requests that give away browserless's address
                service.Environment["HTTPPROXY_STEALTH"] = "on";
                service.Environment["TZ"] = Placeholder(timeZone, resource);
                service.Environment["UPDATER_PERIOD"] = GluetunUpdaterPeriod;
                service.Environment["HTTP_CONTROL_SERVER_AUTH_CONFIG_FILEPATH"] = $"{GluetunAuthDirectory}/{GluetunAuthFileName}";
                service.Restart = "unless-stopped";
                service.CapAdd = ["NET_ADMIN"];
                service.Devices = ["/dev/net/tun:/dev/net/tun"];
                AddVolume(service, GluetunVolume, "/gluetun");
                // gluetun reads these from /run/secrets under these names
                AddSecret(service, OpenVpnUserSecret, "openvpn_user");
                AddSecret(service, OpenVpnPasswordSecret, "openvpn_password");
                AddSecret(service, VpnProxyPasswordSecret, "httpproxy_password");
                service.AddConfig(new ConfigReference
                {
                    Source = GluetunAuthConfigName,
                    Target = $"{GluetunAuthDirectory}/{GluetunAuthFileName}",
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
            // Each service names its own networks, instead of all sharing Aspire's default one
            file.Networks.Clear();
            foreach (var name in InternalNetworks)
            {
                file.Networks[name] = new Network { Name = name, Driver = "bridge", Internal = true };
            }

            foreach (var name in OutwardNetworks)
            {
                file.Networks[name] = new Network { Name = name, Driver = "bridge" };
            }

            foreach (var name in new[] { ScriptsVolume, DatabaseVolume, GluetunVolume })
            {
                file.Volumes[name] = new Volume { Name = name };
            }

            foreach (var name in SecretFileNames)
            {
                file.Secrets[name] = new Secret { Name = name, File = $"./secrets/{name}" };
            }

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
        builder.AddContainer(name, "alpine/socat", "1.8.0.3")
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
    /// An init process as PID 1, for the .NET services. As PID 1 itself, .NET would ignore the SIGABRT its own crash
    /// raises and spin instead of exiting, so a service that failed to start would look like it was running. The init
    /// also reaps the Runner's scripts' processes.
    /// </summary>
    private static void RunUnderInit(Service service) => service.Init = true;

    /// <summary>
    /// A memory limit. Compose applies <c>deploy.resources</c> without Swarm too.
    /// </summary>
    private static void LimitMemory(Service service, string memory) =>
        service.Deploy = new Deploy { Resources = new Resources { Limits = new ResourceSpec { Memory = memory } } };

    private static void AddVolume(Service service, string volume, string target, bool readOnly = false) =>
        service.AddVolume(new Volume { Name = volume, Type = "volume", Source = volume, Target = target, ReadOnly = readOnly });

    private static void AddSecret(Service service, string secret, string target) =>
        service.Secrets.Add(new SecretReference { Source = secret, Target = target });

    private static void DependOn(Service service, string other) =>
        service.DependsOn[other] = new ServiceDependency { Condition = "service_started" };
}
