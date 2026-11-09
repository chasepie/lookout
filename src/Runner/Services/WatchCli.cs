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
/// <param name="Secrets">
/// The secrets in <paramref name="BrowserlessUrl"/> and <paramref name="VpnProxyUrl"/>, as they are and as they appear
/// in URLs, which <see cref="Redact"/> masks.
/// </param>
public sealed record WatchCli(
    string CliPath,
    IReadOnlyList<string> ReadablePaths,
    string BrowserlessUrl,
    string? VpnProxyUrl,
    string OutputDirectory,
    IReadOnlyList<string>? Secrets = null)
{
    /// <summary>
    /// <paramref name="text"/> with <see cref="Secrets"/> masked. A script's output can hold them: Playwright prints the
    /// browserless URL it failed to connect to, token included, and a script can print its own environment.
    /// </summary>
    public string Redact(string text) =>
        (Secrets ?? []).Aggregate(text, (redacted, secret) => redacted.Replace(secret, "***", StringComparison.Ordinal));

    // When it loads, Playwright tests for these to tell whether it runs under WSL. Node's permission model makes the test
    // throw instead of returning false, so reading them must be allowed. On Linux they say nothing beyond that.
    private static readonly string[] PlaywrightLinuxChecks = ["/proc/sys/fs/binfmt_misc/WSLInterop", "/run/WSL"];

    /// <summary>
    /// Finds the CLI under <c>LOOKOUT_RUNNER_WATCH_CLI_ROOT</c>, or the repo root above <paramref name="currentDirectory"/>, and
    /// reads the URLs it needs.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The CLI isn't built, <c>BROWSERLESS_URL</c> or <c>BROWSERLESS_TOKEN</c> isn't set, or either URL
    /// isn't absolute.
    /// </exception>
    public static WatchCli FromConfiguration(IConfiguration configuration, string currentDirectory)
    {
        var root = configuration[EnvVars.WatchCliRoot] is { Length: > 0 } configuredRoot
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
        var browserlessUrl = EnvVars.GetBrowserlessUrl(configuration);
        if (browserlessUrl is null)
        {
            throw new InvalidOperationException(
                $"Environment variable {EnvVars.BrowserlessUrl} must be set: the Runner runs every "
                + "script's browser on browserless.");
        }

        var vpnProxyUrl = EnvVars.GetVpnProxyUrl(configuration);
        var proxyPassword = vpnProxyUrl?.UserInfo.Split(':', 2) is [_, var escapedPassword]
            ? Uri.UnescapeDataString(escapedPassword)
            : null;
        return new WatchCli(
            CliPath: cliPath,
            ReadablePaths: [nodeModules, sdkDirectory, .. OperatingSystem.IsLinux() ? PlaywrightLinuxChecks : []],
            BrowserlessUrl: browserlessUrl,
            VpnProxyUrl: vpnProxyUrl?.AbsoluteUri,
            // A link on macOS
            OutputDirectory: RealPath.Of(Path.GetTempPath()),
            Secrets: SecretForms(configuration[EnvVars.BrowserlessToken], proxyPassword));
    }

    /// <summary>
    /// Each secret as it is and as it appears in a URL, longest first, so masking one form can't leave part of another.
    /// </summary>
    private static string[] SecretForms(params string?[] secrets) =>
        [.. secrets
            .Where(secret => !string.IsNullOrEmpty(secret))
            .SelectMany(secret => new[] { secret!, Uri.EscapeDataString(secret!) })
            .Distinct()
            .OrderByDescending(form => form.Length)];
}
