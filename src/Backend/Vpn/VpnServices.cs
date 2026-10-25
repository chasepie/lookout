using ChasePie.Lookout.Backend.Runner;

namespace ChasePie.Lookout.Backend.Vpn;

public static class VpnServices
{
    /// <summary>
    /// The VPN, and its Rotation. gluetun, when Vpn:ApiKey is set: its control server requires a key on every route
    /// Lookout uses (ADR-0004). Without one, the VPN is disabled and VPN Runs are refused.
    /// </summary>
    public static void AddVpn(this IServiceCollection services, IConfiguration configuration)
    {
        AddRotator(services, configuration);
        services.AddSingleton<VpnRotation>();
        services.AddHostedService(provider => provider.GetRequiredService<VpnRotation>());
    }

    private static void AddRotator(IServiceCollection services, IConfiguration configuration)
    {
        var apiKey = configuration["Vpn:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            services.AddSingleton<IVpnRotator, DisabledVpnRotator>();
            return;
        }

        // Deployment writes the key into gluetun's TOML auth file, where quotes or backslashes would change it.
        if (!apiKey.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new InvalidOperationException("Vpn:ApiKey must contain only letters, digits, '-' and '_', such as from openssl rand -hex 32");
        }

        var proxyAddress = HttpUrls.ConfiguredBaseAddress(
            configuration: configuration,
            key: "Vpn:ProxyUrl",
            defaultUrl: "http://gluetun:8888");

        // Chromium doesn't use credentials written into the proxy's address.
        if (proxyAddress.UserInfo != "")
        {
            throw new InvalidOperationException("Vpn:ProxyUrl must not contain credentials; set Vpn:ProxyUsername and Vpn:ProxyPassword instead");
        }

        // A proxy is a host and port; ConfiguredBaseAddress adds the trailing slash.
        if (proxyAddress.AbsolutePath != "/" || proxyAddress.Query != "")
        {
            throw new InvalidOperationException($"Vpn:ProxyUrl must be only a scheme, host and port, such as http://gluetun:8888, not '{configuration["Vpn:ProxyUrl"]}'");
        }

        var controlAddress = HttpUrls.ConfiguredBaseAddress(
            configuration: configuration,
            key: "Vpn:ControlUrl",
            defaultUrl: "http://gluetun-control:8000");

        services
            .AddSingleton(() => new GluetunSettings(apiKey, new ProxySettings(
                Server: proxyAddress.GetLeftPart(UriPartial.Authority),
                Username: NullIfEmpty(configuration["Vpn:ProxyUsername"]),
                Password: NullIfEmpty(configuration["Vpn:ProxyPassword"]))))
            .AddSingleton(provider => new ProxiedExitIp(
                runner: provider.GetRequiredService<IRunnerClient>,
                timeProvider: provider.GetRequiredService<TimeProvider>()))
            .AddHttpClient<IVpnRotator, GluetunVpnRotator>(client =>
            {
                client.BaseAddress = controlAddress;
                // Reading the status has a shorter deadline of its own (GluetunSettings.StatusTimeout); this bounds a Rotation.
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
