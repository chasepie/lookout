using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChasePie.Lookout.Core;

namespace ChasePie.Lookout.Runner.Services;

public partial class ScriptRunner(
    ILogger<ScriptRunner> logger,
    WatchScriptsDirectory scriptsDirectory,
    IConfiguration configuration)
{
    // How long a cancelled script gets to abort and close its browser before the process is killed
    private static readonly TimeSpan CancelGracePeriod = TimeSpan.FromSeconds(5);

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

        return scriptPath;
    }

    private static string GetWatchCliPath(string repoRoot)
    {
        // npm hoists the workspace SDK into the root node_modules; `bin` points at dist/cli.js
        var cliPath = Path.Combine(repoRoot, "node_modules", "@chasepie", "lookout-watch-sdk", "dist", "cli.js");
        if (!File.Exists(cliPath))
        {
            throw new FileNotFoundException(
                "The lookout-watch CLI was not found. Run `npm install` and `npm run build -w @chasepie/lookout-watch-sdk`.",
                cliPath);
        }

        return cliPath;
    }

    /// <summary>
    /// The proxy that VPN runs send the browser's traffic through.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>LOOKOUT_VPN_PROXY_URL</c> is not set.</exception>
    private string GetVpnProxyUrl()
    {
        var proxyUrl = configuration[LookoutEnvironmentVariables.VpnProxyUrl];
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            // Never run without the VPN when the watch asked for it
            throw new InvalidOperationException(
                $"The watch uses the VPN, but the Runner has no VPN proxy. Set {LookoutEnvironmentVariables.VpnProxyUrl}.");
        }

        return proxyUrl;
    }

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
        var scriptsDir = scriptsDirectory.FullPath;
        var scriptPath = GetScriptPath(scriptsDir, scriptName);
        var cliPath = GetWatchCliPath(RepoRoot.FindRequired(Directory.GetCurrentDirectory()));
        var outputPath = Path.Combine(Path.GetTempPath(), $"lookout-watch-{Guid.NewGuid():N}.json");
        var processStartInfo = CreateStartInfo(cliPath, scriptPath, outputPath, proxyUrl);

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
    /// </summary>
    /// <param name="proxyUrl">
    /// The VPN proxy for a run that uses the VPN, credentials included, or <see langword="null"/> for one that doesn't.
    /// </param>
    internal static ProcessStartInfo CreateStartInfo(string cliPath, string scriptPath, string outputPath, string? proxyUrl)
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
            Environment =
            {
                // The output ends up in logs and in run errors, not a terminal. Playwright colours its errors even
                // when stderr isn't a TTY, and ignores NO_COLOR, so without this the escape codes show up as text.
                ["FORCE_COLOR"] = "0",
            },
        };
        // Scripts have no use for it. This only keeps it out of reach of a careless script, not a malicious one,
        // which runs as the Runner's user and can find it elsewhere; see docs/security.md.
        processStartInfo.Environment.Remove(LookoutEnvironmentVariables.RunnerApiKey);
        // When a package.json without a "type" field is above the scripts directory, as with the repo root in
        // development, Node warns on every ESM script it loads. Node still loads them fine, and the warning would
        // otherwise lead the stderr kept for failed runs.
        processStartInfo.ArgumentList.Add("--disable-warning=MODULE_TYPELESS_PACKAGE_JSON");
        // ArgumentList quotes each argument, so paths with spaces are passed intact
        processStartInfo.ArgumentList.Add(cliPath);
        processStartInfo.ArgumentList.Add("run");
        processStartInfo.ArgumentList.Add(scriptPath);
        processStartInfo.ArgumentList.Add("--output");
        processStartInfo.ArgumentList.Add(outputPath);

        // The CLI reads the proxy from the environment rather than its command line, which any user on the machine
        // can list, since the URL holds the proxy's password. A run without the VPN must not inherit it.
        if (proxyUrl is null)
        {
            processStartInfo.Environment.Remove(LookoutEnvironmentVariables.VpnProxyUrl);
        }
        else
        {
            processStartInfo.Environment[LookoutEnvironmentVariables.VpnProxyUrl] = proxyUrl;
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
