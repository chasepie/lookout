using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChasePie.Lookout.Core;
using Microsoft.AspNetCore.Mvc;

namespace ChasePie.Lookout.Runner.IntegrationTests;

/// <summary>
/// That scripts run in Node's permission model with a clean environment. Each script's top-level code, which runs when
/// the CLI imports it, before it connects to browserless, tries one thing and throws what happened, which the Runner
/// reports as the run's error. Needs Node and the built watch SDK, as the Runner does.
/// </summary>
[TestClass]
public sealed class ScriptSandboxTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    [TestMethod]
    public async Task AScriptCanReadItself()
    {
        var outcome = await RunAsync("readFileSync(new URL(import.meta.url))");

        Assert.AreEqual("allowed", outcome);
    }

    [TestMethod]
    public async Task AScriptCannotReadOtherScripts()
    {
        var outcome = await RunAsync("readFileSync(join(dirname(fileURLToPath(import.meta.url)), 'other.ts'))");

        Assert.AreEqual("ERR_ACCESS_DENIED", outcome);
    }

    [TestMethod]
    public async Task AScriptThatIsALinkIsRefused()
    {
        await using var factory = new RunnerFactory();
        Directory.CreateDirectory(factory.ScriptsDirectory);
        var target = Path.Join(factory.DataDirectory, "elsewhere.ts");
        await File.WriteAllTextAsync(target, "export default {};", Ct);
        File.CreateSymbolicLink(Path.Join(factory.ScriptsDirectory, "linked.ts"), target);
        using var client = factory.CreateClient(RunnerFactory.ApiKey);

        using var response = await client.GetAsync("/WatchRun?scriptName=linked.ts", Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task AScriptCannotReadOutsideItsDirectories()
    {
        var outcome = await RunAsync("readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'secret.txt'))");

        Assert.AreEqual("ERR_ACCESS_DENIED", outcome);
    }

    [TestMethod]
    public async Task AScriptCannotWriteFiles()
    {
        var outcome = await RunAsync("writeFileSync(join(dirname(fileURLToPath(import.meta.url)), 'written.txt'), 'x')");

        Assert.AreEqual("ERR_ACCESS_DENIED", outcome);
    }

    [TestMethod]
    public async Task AScriptCannotStartProcesses()
    {
        var outcome = await RunAsync("spawnSync(process.execPath, ['--version'])");

        Assert.AreEqual("ERR_ACCESS_DENIED", outcome);
    }

    [TestMethod]
    public async Task AScriptCannotStartWorkerThreads()
    {
        var outcome = await RunAsync("new Worker('', { eval: true })");

        Assert.AreEqual("ERR_ACCESS_DENIED", outcome);
    }

    [TestMethod]
    public async Task AScriptSeesOnlyTheVariablesTheCliNeeds()
    {
        var outcome = await RunAsync("(() => { throw new Error(Object.keys(process.env).sort().join(',')); })()");

        // macOS adds __CF_USER_TEXT_ENCODING to every process; TZ is passed through when the Runner has it
        var names = outcome.Split(',').Except(["__CF_USER_TEXT_ENCODING", "TZ"]);
        Assert.AreSequenceEqual(new[] { "FORCE_COLOR", LookoutEnvironmentVariables.BrowserlessUrl }, names);
    }

    [TestMethod]
    public void TheRunnerRefusesToStartWithoutBrowserless()
    {
        using var factory = new RunnerFactory(browserlessUrl: null);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Server);

        Assert.Contains(LookoutEnvironmentVariables.BrowserlessUrl, ex.Message);
    }

    [TestMethod]
    public void TheRunnerRefusesToStartWithoutABrowserlessToken()
    {
        // browserless accepts anyone without one, so it must have been started with one too
        using var factory = new RunnerFactory(browserlessToken: null);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.Server);

        Assert.Contains(LookoutEnvironmentVariables.BrowserlessToken, ex.Message);
    }

    [TestMethod]
    public void ATokenInTheBrowserlessUrlIsEnough()
    {
        using var factory = new RunnerFactory(browserlessUrl: RunnerFactory.BrowserlessUrl + "?token=abc", browserlessToken: null);

        Assert.IsNotNull(factory.Server);
    }

    /// <summary>
    /// Runs a script that evaluates <paramref name="expression"/> at its top level, and returns "allowed" if it
    /// succeeded, or else its error's code, or its message if it has none.
    /// </summary>
    private async Task<string> RunAsync(string expression)
    {
        await using var factory = new RunnerFactory();
        Directory.CreateDirectory(factory.ScriptsDirectory);
        await File.WriteAllTextAsync(Path.Join(factory.DataDirectory, "secret.txt"), "secret", Ct);
        await File.WriteAllTextAsync(Path.Join(factory.ScriptsDirectory, "other.ts"), "export default {};", Ct);
        await File.WriteAllTextAsync(Path.Join(factory.ScriptsDirectory, "sandbox.ts"), $$"""
            import { spawnSync } from 'node:child_process';
            import { readFileSync, writeFileSync } from 'node:fs';
            import { dirname, join } from 'node:path';
            import { fileURLToPath } from 'node:url';
            import { Worker } from 'node:worker_threads';

            let outcome: string;
            try {
              const result = {{expression}};
              // spawnSync reports a refusal in its result instead of throwing
              const error = (result as { error?: { code?: string } } | undefined)?.error;
              outcome = error ? (error.code ?? String(error)) : 'allowed';
            } catch (error) {
              outcome = (error as { code?: string }).code ?? (error as Error).message;
            }

            throw new Error(`OUTCOME[${outcome}]`);
            """, Ct);
        using var client = factory.CreateClient(RunnerFactory.ApiKey);

        using var response = await client.GetAsync("/WatchRun?scriptName=sandbox.ts", Ct);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(JsonSerializerOptions.Web, Ct);
        var detail = problem?.Detail ?? "";
        var start = detail.IndexOf("OUTCOME[", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, detail);
        start += "OUTCOME[".Length;
        return detail[start..detail.IndexOf(']', start)];
    }
}
