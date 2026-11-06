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
    /// shell that happens to have <c>LOOKOUT_VPN_PROXY_URL</c> doesn't send direct CLI runs through the VPN.
    /// </summary>
    internal const string CliProxyUrlVariable = "LOOKOUT_WATCH_PROXY_URL";

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
    /// <exception cref="InvalidOperationException"><c>LOOKOUT_VPN_PROXY_URL</c> is not set.</exception>
    private string GetVpnProxyUrl() =>
        // Never run without the VPN when the watch asked for it
        watchCli.VpnProxyUrl ?? throw new InvalidOperationException(
            $"The watch uses the VPN, but the Runner has no VPN proxy. Set {LookoutEnvironmentVariables.VpnProxyUrl}.");

    /// <param name="useVpn">Whether the browser's traffic goes through the VPN proxy.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="useVpn"/> is set but there is no VPN proxy, or the CLI failed.
    /// </exception>
    public async Task<WatchRunResult> RunWatchScriptAsync(
        string scriptName,
        bool useVpn,
        CancellationToken cancellationToken = default)
    {
        var proxyUrl = useVpn ? GetVpnProxyUrl() : null;
        // Both directories are real paths already, and the script isn't a link, as Node's permission model needs
        var scriptPath = GetScriptPath(scriptsDirectory.FullPath, scriptName);
        var outputPath = Path.Combine(watchCli.OutputDirectory, $"lookout-watch-{Guid.NewGuid():N}.json");
        var processStartInfo = CreateStartInfo(watchCli, scriptPath, outputPath, proxyUrl);

        using var process = new Process { StartInfo = processStartInfo };

        // stdout/stderr are only logs (the result goes to the output file), so scripts can console.log freely
        var stderrTail = new Queue<string>();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                LogScriptOutput(scriptName, args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            LogScriptError(scriptName, args.Data);
            lock (stderrTail)
            {
                stderrTail.Enqueue(args.Data);
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

            if (process.ExitCode != 0)
            {
                string stderr;
                lock (stderrTail)
                {
                    stderr = string.Join(Environment.NewLine, stderrTail);
                }

                throw new InvalidOperationException($"lookout-watch exited with code {process.ExitCode}: {stderr}");
            }

            var json = await File.ReadAllTextAsync(outputPath, cancellationToken);
            try
            {
                return JsonSerializer.Deserialize<WatchRunResult>(json, JsonOptions)
                    ?? throw new JsonException("The output was 'null'.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"lookout-watch wrote output that isn't a run result: {json}", ex);
            }
        }
        finally
        {
            File.Delete(outputPath);
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
    internal static ProcessStartInfo CreateStartInfo(WatchCli cli, string scriptPath, string outputPath, string? proxyUrl)
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
        environment[LookoutEnvironmentVariables.BrowserlessUrl] = cli.BrowserlessUrl;
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
