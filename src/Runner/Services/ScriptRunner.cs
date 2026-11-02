using System;
using System.Diagnostics;
using System.Text.Json;

namespace ChasePie.Lookout.Runner.Services;

public class ScriptRunner
{
    private const string WatchScriptsDir = "watch-scripts";

    // camelCase + case-insensitive, matching the CLI's JSON output
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string RepoRoot()
    {
        var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (currentDir != null && currentDir.GetFiles("Lookout.slnx").Length == 0)
        {
            currentDir = currentDir.Parent;
        }

        if (currentDir == null)
        {
            throw new InvalidOperationException("Could not find the repository root directory.");
        }

        return currentDir.FullName;
    }

    private static string GetScriptPath(string scriptName)
    {
        var repoRoot = RepoRoot();
        var scriptPath = Path.Combine(repoRoot, WatchScriptsDir, scriptName);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Script '{scriptName}' not found in the '{WatchScriptsDir}' directory.");
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

    public async Task<WatchRunResult> RunWatchScriptAsync(
        string scriptName,
        CancellationToken cancellationToken = default)
    {
        var repoRoot = RepoRoot();
        var scriptPath = GetScriptPath(scriptName);
        var cliPath = GetWatchCliPath(repoRoot);

        var processStartInfo = new ProcessStartInfo("node")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // ArgumentList quotes each argument, so paths with spaces are passed intact
        processStartInfo.ArgumentList.Add(cliPath);
        processStartInfo.ArgumentList.Add("run");
        processStartInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("Failed to start the node process.");

        // Read both streams concurrently so neither pipe's buffer fills up and blocks the CLI
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Also kills the Chromium processes Playwright launched
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"lookout-watch exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        try
        {
            return JsonSerializer.Deserialize<WatchRunResult>(stdout, JsonOptions)
                ?? throw new JsonException("The output was 'null'.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"lookout-watch printed output that isn't a run result: {stdout}", ex);
        }
    }
}
