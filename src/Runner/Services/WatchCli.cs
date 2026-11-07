using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Runner.Services;

/// <summary>
/// The watch CLI that runs scripts, and the settings it runs them with. Resolve it once at startup with
/// <see cref="FromConfiguration"/>, so a missing setting stops the Runner instead of failing each run.
/// </summary>
/// <param name="CliPath">
/// The CLI's real path. Node's permission model refuses to load it through the <c>node_modules</c> link npm makes for
/// the workspace.
/// </param>
/// <param name="ReadablePaths">
/// What scripts may read besides the scripts directory, as real paths: the <c>node_modules</c> the CLI and scripts
/// import from, the SDK's own directory, and on Linux the files Playwright checks for when it loads.
/// </param>
/// <param name="BrowserlessUrl">Where scripts run their browser, with browserless's token.</param>
/// <param name="VpnProxyUrl">
/// The proxy that runs using the VPN send their browser's traffic through, with its password, if there is one.
/// </param>
/// <param name="OutputDirectory">Where the CLI writes each run's result, as a real path: the temp directory.</param>
public sealed record WatchCli(
    string CliPath,
    IReadOnlyList<string> ReadablePaths,
    string BrowserlessUrl,
    string? VpnProxyUrl,
    string OutputDirectory)
{
    // When it loads, Playwright tests for these to tell whether it runs under WSL. Node's permission model makes the test
    // throw instead of returning false, so reading them must be allowed. On Linux they say nothing beyond that.
    private static readonly string[] PlaywrightLinuxChecks = ["/proc/sys/fs/binfmt_misc/WSLInterop", "/run/WSL"];

    /// <summary>
    /// Finds the CLI under <c>LOOKOUT_WATCH_CLI_ROOT</c>, or the repo root above <paramref name="currentDirectory"/>, and
    /// reads the URLs it needs.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The CLI isn't built, <c>LOOKOUT_BROWSERLESS_URL</c> isn't set or has no token, or it or <c>LOOKOUT_VPN_PROXY_URL</c>
    /// isn't an absolute URL.
    /// </exception>
    public static WatchCli FromConfiguration(IConfiguration configuration, string currentDirectory)
    {
        var root = configuration[LookoutEnvironmentVariables.WatchCliRoot] is { Length: > 0 } configuredRoot
            ? Path.GetFullPath(configuredRoot)
            : RepoRoot.FindRequired(currentDirectory);
        var nodeModules = RealPath.Of(Path.Combine(root, "node_modules"));

        // npm links the workspace SDK into node_modules; `bin` points at dist/cli.js
        var sdkDirectory = RealPath.Of(Path.Combine(nodeModules, "@chasepie", "lookout-watch-sdk"));
        var cliPath = Path.Combine(sdkDirectory, "dist", "cli.js");
        if (!File.Exists(cliPath))
        {
            throw new InvalidOperationException(
                $"The lookout-watch CLI was not found at {cliPath}. Run `npm install` and "
                + "`npm run build -w @chasepie/lookout-watch-sdk`.");
        }

        // Scripts run sandboxed, and the sandbox can't start a local browser
        var browserlessUrl = LookoutEnvironmentVariables.GetBrowserlessUrl(configuration);
        if (browserlessUrl is null)
        {
            throw new InvalidOperationException(
                $"Environment variable {LookoutEnvironmentVariables.BrowserlessUrl} must be set: the Runner runs every "
                + "script's browser on browserless.");
        }

        // Without a token browserless accepts anyone, so a Runner without one means a browserless open to the network
        if (!HasToken(browserlessUrl))
        {
            throw new InvalidOperationException(
                $"Environment variable {LookoutEnvironmentVariables.BrowserlessToken} must be set to browserless's token.");
        }

        return new WatchCli(
            CliPath: cliPath,
            ReadablePaths: [nodeModules, sdkDirectory, .. OperatingSystem.IsLinux() ? PlaywrightLinuxChecks : []],
            BrowserlessUrl: browserlessUrl,
            VpnProxyUrl: LookoutEnvironmentVariables.GetVpnProxyUrl(configuration)?.AbsoluteUri,
            // A link on macOS
            OutputDirectory: RealPath.Of(Path.GetTempPath()));
    }

    private static bool HasToken(string browserlessUrl)
    {
        return Uri.TryCreate(browserlessUrl, UriKind.Absolute, out var url)
            && url.Query.TrimStart('?').Split('&')
                .Any(parameter =>
                    parameter.StartsWith("token=", StringComparison.Ordinal)
                    && parameter.Length > "token=".Length);
    }
}
