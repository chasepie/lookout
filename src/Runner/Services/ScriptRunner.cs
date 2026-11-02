using System;
using System.Diagnostics;

namespace ChasePie.Lookout.Runner.Services;

public class ScriptRunner
{
    private const string WatchScriptsDir = "watch-scripts";

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

    public Task RunWatchScriptAsync(
        string scriptName,
        CancellationToken cancellationToken = default)
    {
        var scriptPath = GetScriptPath(scriptName);
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = scriptPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process
        {
            StartInfo = processStartInfo,
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (sender, args) =>
        {
            if (args.Data != null)
            {
                Console.WriteLine(args.Data);
            }
        };

        process.ErrorDataReceived += (sender, args) =>
        {
            if (args.Data != null)
            {
                Console.Error.WriteLine(args.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return Task.Run(() =>
        {
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(100);
            }
        }, cancellationToken);
    }
}
