using System.Globalization;
using Aspire.Hosting.Docker;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.AppHost;

public partial class Program
{
    /// <summary>
    /// The deployment's settings, which <c>aspire publish</c> lists in <c>deploy/.env</c>. Ones with a default fall back to
    /// it when left empty, as <c>${NAME:-default}</c>. Required ones have none, and the service that needs one refuses to
    /// start without it; one Compose reads itself, such as an image's tag, has Compose stop with a message instead, as
    /// <c>${NAME:?message}</c>.
    /// </summary>
    private sealed class ProductionSettings
    {
        // For the secrets the Owner chooses: Compose expands $NAME in .env values, and # starts a comment, unless quoted
        private const string QuoteIfNeeded = "Put it in single quotes if it has $, # or spaces in it.";

        private readonly IDistributedApplicationBuilder _builder;
        private readonly Dictionary<string, string> _defaults = [];
        private readonly Dictionary<string, string> _composeRequiredMessages = [];

        public ProductionSettings(IDistributedApplicationBuilder builder)
        {
            _builder = builder;

            ImageTag = Setting(
                name: "lookout-image-tag",
                defaultValue: null,
                composeRequiredMessage: "set it in .env to the release to run, such as 0.1",
                description: "the tag of the Backend's and the Runner's images: a release's major version, such as 1, or before 1.0 its "
                    + "minor version, such as 0.4, to follow its updates; or a version, such as 0.4.3, to stay on it.");

            Host = Setting(
                name: "lookout-host",
                defaultValue: null,
                description: "the hostname Lookout is served on, such as lookout.example.com. Requests for any other are refused.");

            BindAddress = Setting(
                name: "lookout-bind-address",
                defaultValue: "127.0.0.1",
                description: "The host address the Backend's port is published on: 127.0.0.1 for a reverse proxy on this machine.");

            Port = Setting(
                name: "lookout-port",
                defaultValue: "8080",
                description: "The host port the Backend is published on.");

            TrustedProxies = Setting(
                name: EnvVars.LookoutTrustedProxies.ToKebabCase(),
                defaultValue: "",
                description: "The reverse proxy's address as the Backend sees it, as comma-separated IPs or CIDR networks. Without it, "
                    + "X-Forwarded-For and X-Forwarded-Proto are ignored, and signing in through the proxy fails.");

            OwnerUsername = Setting(
                name: EnvVars.LookoutOwnerUsername.ToKebabCase(),
                defaultValue: null,
                description: "the username the Owner signs in with.");

            TimeZone = Setting(
                name: SettingNames.TimeZone.ToKebabCase(),
                defaultValue: SettingNames.DefaultTimeZone,
                description: "The time zone of schedules and scripts' dates, such as America/New_York.");

            EdgeSubnet = Setting(
                name: "lookout-edge-subnet",
                defaultValue: "172.30.240.0/24",
                description: "The subnet of the Backend's edge network, where its published port's connections come from; its gateway is "
                    + "the subnet's first address, 172.30.240.1 by default. Fixed, so the gateway stays the same when the network is "
                    + "re-created. Change it if it overlaps another network on this machine.");

            VpnServiceProvider = Setting(
                name: SettingNames.GluetunVpnServiceProvider.ToKebabCase(),
                defaultValue: SettingNames.DefaultVpnServiceProvider,
                description: "gluetun's VPN_SERVICE_PROVIDER.");

            ServerRegions = Setting(
                name: EnvVars.GluetunServerRegions.ToKebabCase(),
                defaultValue: "",
                description: "The VPN regions to use, as gluetun's SERVER_REGIONS: comma-separated with no spaces around the commas, "
                    + "such as 'DE Berlin,US Texas'. gluetun connects to one of them, and the Backend switches and rotates only "
                    + "among them. Without it, every region of the provider.");

            MaxConcurrentRuns = Setting(
                name: EnvVars.LookoutMaxConcurrentRuns.ToKebabCase(),
                defaultValue: EnvVars.DefaultMaxConcurrentRuns.ToString(CultureInfo.InvariantCulture),
                description: "How many watch runs Lookout runs at once at most, each of a different watch; browserless runs as many "
                    + "browser sessions at once. Each takes memory in browserless and the Runner, so raise BROWSERLESS_MEMORY and LOOKOUT_RUNNER_MEMORY with it.");

            RunnerMemory = Setting(
                name: "lookout-runner-memory",
                defaultValue: "1g",
                description: "The Runner's memory limit, which bounds what a script can take.");

            BrowserlessMemory = Setting(
                name: "browserless-memory",
                defaultValue: "2g",
                description: "browserless's memory limit.");

            OwnerPassword = Secret(
                name: EnvVars.LookoutOwnerPassword.ToKebabCase(),
                description: $"the password the Owner signs in with, at least 16 characters. Changing it signs out every session. {QuoteIfNeeded}");

            RunnerApiKey = Secret(
                name: EnvVars.LookoutRunnerApiKey.ToKebabCase(),
                description: "the key the Backend sends the Runner, such as from `openssl rand -hex 32`.");

            BrowserlessToken = Secret(
                name: EnvVars.BrowserlessToken.ToKebabCase(),
                description: "browserless's token, such as from `openssl rand -hex 32`.");

            GluetunApiKey = Secret(
                name: EnvVars.GluetunApiKey.ToKebabCase(),
                description: "the Backend's key for gluetun's control server, letters and digits only, such as from `openssl rand -hex 32`.");

            ProxyPassword = Secret(
                name: EnvVars.GluetunProxyPassword.ToKebabCase(),
                description: "the password of gluetun's HTTP proxy, such as from `openssl rand -hex 32`.");

            OpenVpnUser = Secret(
                name: SettingNames.GluetunOpenVpnUser.ToKebabCase(),
                description: $"your VPN provider's OpenVPN username. {QuoteIfNeeded}");

            OpenVpnPassword = Secret(
                name: SettingNames.GluetunOpenVpnPassword.ToKebabCase(),
                description: $"your VPN provider's OpenVPN password. {QuoteIfNeeded}");
        }

        public IResourceBuilder<ParameterResource> ImageTag { get; }
        public IResourceBuilder<ParameterResource> Host { get; }
        public IResourceBuilder<ParameterResource> BindAddress { get; }
        public IResourceBuilder<ParameterResource> Port { get; }
        public IResourceBuilder<ParameterResource> TrustedProxies { get; }
        public IResourceBuilder<ParameterResource> OwnerUsername { get; }
        public IResourceBuilder<ParameterResource> TimeZone { get; }
        public IResourceBuilder<ParameterResource> EdgeSubnet { get; }
        public IResourceBuilder<ParameterResource> VpnServiceProvider { get; }
        public IResourceBuilder<ParameterResource> ServerRegions { get; }
        public IResourceBuilder<ParameterResource> MaxConcurrentRuns { get; }
        public IResourceBuilder<ParameterResource> RunnerMemory { get; }
        public IResourceBuilder<ParameterResource> BrowserlessMemory { get; }

        // Secrets
        public IResourceBuilder<ParameterResource> OwnerPassword { get; }
        public IResourceBuilder<ParameterResource> RunnerApiKey { get; }
        public IResourceBuilder<ParameterResource> BrowserlessToken { get; }
        public IResourceBuilder<ParameterResource> GluetunApiKey { get; }
        public IResourceBuilder<ParameterResource> ProxyPassword { get; }
        public IResourceBuilder<ParameterResource> OpenVpnUser { get; }
        public IResourceBuilder<ParameterResource> OpenVpnPassword { get; }

        /// <summary>
        /// The edge network's subnet as a Compose variable, read from the Backend's service for the network; set when
        /// the Backend's service is published.
        /// </summary>
        public string? EdgeSubnetPlaceholder { get; set; }

        /// <summary>
        /// gluetun's key as a Compose variable, for its auth config; set when gluetun's service is published.
        /// </summary>
        public string? GluetunApiKeyPlaceholder { get; set; }

        /// <summary>
        /// <paramref name="setting"/> as a Compose variable in <paramref name="service"/>, with its default if it has one, or
        /// the message Compose stops with if it requires it.
        /// </summary>
        public string Placeholder(IResourceBuilder<ParameterResource> setting, DockerComposeServiceResource service)
        {
            var placeholder = setting.AsEnvironmentPlaceholder(service);
            if (_defaults.TryGetValue(setting.Resource.Name, out var defaultValue))
            {
                return $"{placeholder[..^1]}:-{defaultValue}}}";
            }

            if (_composeRequiredMessages.TryGetValue(setting.Resource.Name, out var message))
            {
                return $"{placeholder[..^1]}:?{message}}}";
            }

            return placeholder;
        }

        /// <param name="composeRequiredMessage">
        /// For a required setting Compose reads itself rather than a service, such as an image's tag: what Compose says
        /// when it's empty, where it would otherwise go on with an empty value, such as an image reference ending in a
        /// colon. No <c>}</c> or <c>$</c>.
        /// </param>
        private IResourceBuilder<ParameterResource> Setting(
            string name, string? defaultValue, string description, bool secret = false, string? composeRequiredMessage = null)
        {
            if (composeRequiredMessage is not null)
            {
                _composeRequiredMessages[name] = composeRequiredMessage;
            }

            if (defaultValue is not null)
            {
                _defaults[name] = defaultValue;
                description += $" Default: {(defaultValue.Length > 0 ? defaultValue : "none")}.";
            }
            else
            {
                description = "Required: " + description;
            }

            return _builder
                .AddParameter(name, secret: secret)
                .WithDescription(description);
        }

        private IResourceBuilder<ParameterResource> Secret(string name, string description) =>
            Setting(name, defaultValue: null, description, secret: true);
    }
}
