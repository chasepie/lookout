using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Runner.Services;

/// <summary>
/// Runs watch scripts with the watch CLI, in Node's permission model and a clean environment; see docs/security.md.
/// </summary>
public partial class ScriptRunner(
    ILogger<ScriptRunner> logger,
    WatchScriptsDirectory scriptsDirectory,
    WatchCli watchCli)
{
    // How long a cancelled script gets to abort and close its browser before the process is killed
    private static readonly TimeSpan CancelGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The variable the watch CLI reads its proxy from. Only the Runner sets it, and only for runs that use the VPN, so a
    /// shell that happens to have <c>GLUETUN_PROXY_URL</c> doesn't send direct CLI runs through the VPN.
    /// </summary>
    internal const string CliProxyUrlVariable = "LOOKOUT_WATCH_PROXY_URL";

    /// <summary>
    /// The variable the watch CLI reads the browserless server from. The same name as the Runner's
    /// <see cref="EnvVars.BrowserlessUrl"/>, but part of the CLI's interface, since it's also run by hand.
    /// </summary>
    internal const string CliBrowserlessUrlVariable = "BROWSERLESS_URL";

    // Variables scripts get from the Runner's own environment, when it has them. Neither is secret: TZ keeps a script's
    // dates in the configured time zone, and Node can't open network connections on Windows without SystemRoot.
    private static readonly string[] PassedThroughVariables = ["TZ", "SystemRoot"];

    // How many trailing stderr lines to include when the CLI fails
    private const int StderrTailLines = 20;

    // camelCase + case-insensitive, matching the CLI's JSON output
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Resolves a script directly inside <paramref name="scriptsDir"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="scriptName"/> is not a bare file name.</exception>
    private static string GetScriptPath(string scriptsDir, string scriptName)
    {
        // Only files directly inside the scripts directory may run
        if (string.IsNullOrWhiteSpace(scriptName) || Path.GetFileName(scriptName) != scriptName)
        {
            throw new ArgumentException($"'{scriptName}' is not a script file name.", nameof(scriptName));
        }

        var scriptPath = Path.Combine(scriptsDir, scriptName);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Script '{scriptName}' not found in the '{scriptsDir}' directory.");
        }

        // The Backend only saves regular files. A link would point the script's read permission somewhere else.
        if (new FileInfo(scriptPath).LinkTarget is not null)
        {
            throw new ArgumentException($"'{scriptName}' is a link; only regular files can run.", nameof(scriptName));
        }

        return scriptPath;
    }

    /// <summary>
    /// The proxy that VPN runs send the browser's traffic through.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>GLUETUN_PROXY_URL</c> is not set.</exception>
    private string GetVpnProxyUrl() =>
        // Never run without the VPN when the watch asked for it
        watchCli.VpnProxyUrl ?? throw new InvalidOperationException(
            $"The watch uses the VPN, but the Runner has no VPN proxy. Set {EnvVars.GluetunProxyUrl}.");

    /// <param name="useVpn">Whether the browser's traffic goes through the VPN proxy.</param>
    /// <param name="screenshots">When the CLI takes a screenshot of its own.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="useVpn"/> is set but there is no VPN proxy, or the CLI failed.
    /// </exception>
    public async Task<WatchRunResult> RunWatchScriptAsync(
        string scriptName,
        bool useVpn,
        ScreenshotSettings screenshots,
        CancellationToken cancellationToken = default)
    {
        var proxyUrl = useVpn ? GetVpnProxyUrl() : null;
        // Both directories are real paths already, and the script isn't a link, as Node's permission model needs
        var scriptPath = GetScriptPath(scriptsDirectory.FullPath, scriptName);
        var outputPath = Path.Combine(watchCli.OutputDirectory, $"lookout-watch-{Guid.NewGuid():N}.json");
        var processStartInfo = CreateStartInfo(watchCli, scriptPath, outputPath, proxyUrl, screenshots);

        using var process = new Process { StartInfo = processStartInfo };

        // stdout/stderr are only logs (the result goes to the output file), so scripts can console.log freely
        var stderrTail = new Queue<string>();
        process.OutputDataReceived += (_, args) =>
        {
            // Redacted only when it's logged
            if (args.Data is not null && logger.IsEnabled(LogLevel.Information))
            {
                var line = watchCli.Redact(args.Data);
                LogScriptOutput(scriptName, line);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            var line = watchCli.Redact(args.Data);
            LogScriptError(scriptName, line);
            lock (stderrTail)
            {
                stderrTail.Enqueue(line);
                if (stderrTail.Count > StderrTailLines)
                {
                    stderrTail.Dequeue();
                }
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await StopAsync(process);
                throw;
            }

            // A crash is written like a result, so its screenshot gets out too. Without the file, the CLI failed before
            // the script ran, and only its stderr says why; likewise if it died writing the file.
            WatchRunResult? result = null;
            if (File.Exists(outputPath))
            {
                try
                {
                    result = await ReadResultAsync(outputPath, cancellationToken);
                }
                catch (InvalidOperationException) when (process.ExitCode != 0)
                {
                    // Reported with stderr below
                }
            }
            if (result is not null && (process.ExitCode == 0 || result.Crash is not null))
            {
                // A crash's error and a known failure's reason can hold secrets, as stderr can: Playwright's errors
                // name the browserless URL, and a script can make one its reason
                return result with
                {
                    FailureReason = result.FailureReason is { } reason
                        ? watchCli.Redact(reason)
                        : null,
                    Crash = result.Crash is { } crash
                        ? crash with { Error = watchCli.Redact(crash.Error) }
                        : null,
                };
            }

            string stderr;
            lock (stderrTail)
            {
                stderr = string.Join(Environment.NewLine, stderrTail);
            }

            throw new InvalidOperationException($"lookout-watch exited with code {process.ExitCode}: {stderr}");
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    /// <exception cref="InvalidOperationException">The file isn't a run result.</exception>
    private async Task<WatchRunResult> ReadResultAsync(string outputPath, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(outputPath, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<WatchRunResult>(json, JsonOptions)
                ?? throw new JsonException("The output was 'null'.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"lookout-watch wrote output that isn't a run result: {watchCli.Redact(json)}", ex);
        }
    }

    /// <summary>
    /// The Node process that runs a script with the watch CLI, writing its result to <paramref name="outputPath"/>.
    /// The script gets a clean environment, and Node's permission model limits it to reading itself and
    /// <see cref="WatchCli.ReadablePaths"/>, writing only <paramref name="outputPath"/>, and no child processes or
    /// worker threads. See docs/security.md.
    /// </summary>
    /// <param name="proxyUrl">
    /// The VPN proxy for a run that uses the VPN, credentials included, or <see langword="null"/> for one that doesn't.
    /// </param>
    /// <param name="screenshots">When the CLI takes a screenshot of its own; none when <see langword="null"/>.</param>
    internal static ProcessStartInfo CreateStartInfo(
        WatchCli cli,
        string scriptPath,
        string outputPath,
        string? proxyUrl,
        ScreenshotSettings? screenshots = null)
    {
        var processStartInfo = new ProcessStartInfo("node")
        {
            // The scripts directory. The CLI is started by absolute path and resolves its own imports, so it can run
            // from anywhere.
            WorkingDirectory = Path.GetDirectoryName(scriptPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Only what the CLI needs, so the Runner's secrets, and anything else in its environment, don't reach scripts
        var environment = processStartInfo.Environment;
        environment.Clear();
        // The output ends up in logs and in run errors, not a terminal. Playwright colours its errors even when stderr
        // isn't a TTY, and ignores NO_COLOR, so without this the escape codes show up as text.
        environment["FORCE_COLOR"] = "0";
        environment[CliBrowserlessUrlVariable] = cli.BrowserlessUrl;
        // The CLI reads the proxy from the environment rather than its command line, which any user on the machine
        // can list, since the URL holds the proxy's password. Only a run that uses the VPN gets it.
        if (proxyUrl is not null)
        {
            environment[CliProxyUrlVariable] = proxyUrl;
        }

        foreach (var name in PassedThroughVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                environment[name] = value;
            }
        }

        // ArgumentList quotes each argument, so paths with spaces are passed intact
        var arguments = processStartInfo.ArgumentList;
        // Without --allow-child-process and --allow-worker, neither is allowed. Each path needs a flag of its own: Node
        // takes a comma-separated list as one path.
        arguments.Add("--permission");
        // The script itself, not the scripts directory, so it can't read other watches' scripts
        arguments.Add($"--allow-fs-read={scriptPath}");
        foreach (var path in cli.ReadablePaths)
        {
            arguments.Add($"--allow-fs-read={path}");
        }

        arguments.Add($"--allow-fs-write={outputPath}");
        // The CLI must reach browserless, and Node can't limit the network to one host
        arguments.Add("--allow-net");
        // --allow-net is experimental, and Node says so on every run. When a package.json without a "type" field is
        // above the scripts directory, as with the repo root in development, Node also warns on every ESM script it
        // loads. Both would otherwise lead the stderr kept for failed runs.
        arguments.Add("--disable-warning=ExperimentalWarning");
        arguments.Add("--disable-warning=MODULE_TYPELESS_PACKAGE_JSON");
        arguments.Add(cli.CliPath);
        arguments.Add("run");
        arguments.Add(scriptPath);
        arguments.Add("--output");
        arguments.Add(outputPath);
        if (screenshots?.OnSuccess == true)
        {
            arguments.Add("--screenshot-on-success");
        }

        if (screenshots?.OnKnownFailure == true)
        {
            arguments.Add("--screenshot-on-known-failure");
        }

        if (screenshots?.OnCrash == true)
        {
            arguments.Add("--screenshot-on-crash");
        }

        return processStartInfo;
    }

    /// <summary>
    /// Asks the CLI to abort with SIGTERM, then kills it if it hasn't exited within <see cref="CancelGracePeriod"/>.
    /// </summary>
    private static async Task StopAsync(Process process)
    {
        try
        {
            process.SendSignal(PosixSignal.SIGTERM);
        }
        catch (NotImplementedException)
        {
            // Windows: don't leave the CLI (and its browser) running while the exception surfaces
            process.Kill(entireProcessTree: true);
            throw;
        }

        using var graceTimeout = new CancellationTokenSource(CancelGracePeriod);
        try
        {
            await process.WaitForExitAsync(graceTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ScriptName}] {Line}")]
    private partial void LogScriptOutput(string scriptName, string line);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{ScriptName}] {Line}")]
    private partial void LogScriptError(string scriptName, string line);
}
