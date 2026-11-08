using System.Diagnostics;
using ChasePie.Lookout.Core;
using ChasePie.Lookout.Runner.Services;

namespace ChasePie.Lookout.Runner.UnitTests;

// The tests set the Runner's environment variables, which are shared by the whole process
[TestClass]
[DoNotParallelize]
public sealed class ScriptRunnerStartInfoTests
{
    private const string ProxyPassword = "secret";
    private const string ProxyUrl = $"http://lookout:{ProxyPassword}@gluetun:8888";
    private const string BrowserlessUrl = "http://browserless:3000?token=browserless-token";

    private static readonly WatchCli Cli = new(
        "/app/src/watch-sdk/dist/cli.js", ["/app/node_modules", "/app/src/watch-sdk"], BrowserlessUrl, ProxyUrl, "/tmp");

    // What the Runner's own environment holds when the AppHost starts it, plus a CLI proxy left over from a shell
    private static readonly Dictionary<string, string> RunnerEnvironment = new()
    {
        [EnvironmentVariables.GluetunProxyUrl] = ProxyUrl,
        [EnvironmentVariables.LookoutRunnerApiKey] = new string('k', RunnerApiKey.MinLength),
        [ScriptRunner.CliProxyUrlVariable] = ProxyUrl,
        ["HOME"] = "/home/app",
        ["TZ"] = "America/New_York",
    };

    private static readonly Dictionary<string, string?> PreviousEnvironment = [];

    [ClassInitialize]
    public static void SetRunnerEnvironment(TestContext _)
    {
        foreach (var (name, value) in RunnerEnvironment)
        {
            PreviousEnvironment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    [ClassCleanup]
    public static void RestoreEnvironment()
    {
        foreach (var (name, value) in PreviousEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static ProcessStartInfo Create(string? proxyUrl) =>
        ScriptRunner.CreateStartInfo(Cli, "/app/scripts/watch.ts", "/tmp/out.json", proxyUrl);

    [TestMethod]
    public void ARunWithoutTheVpnGetsOnlyWhatTheCliNeeds()
    {
        var startInfo = Create(proxyUrl: null);

        Assert.AreSequenceEqual(
            new Dictionary<string, string?>
            {
                ["FORCE_COLOR"] = "0",
                [ScriptRunner.CliBrowserlessUrlVariable] = BrowserlessUrl,
                ["TZ"] = "America/New_York",
            },
            startInfo.Environment,
            SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void AVpnRunAlsoGetsTheProxyInItsEnvironmentAndNotOnItsCommandLine()
    {
        var startInfo = Create(ProxyUrl);

        Assert.AreSequenceEqual(
            new Dictionary<string, string?>
            {
                ["FORCE_COLOR"] = "0",
                [ScriptRunner.CliBrowserlessUrlVariable] = BrowserlessUrl,
                [ScriptRunner.CliProxyUrlVariable] = ProxyUrl,
                ["TZ"] = "America/New_York",
            },
            startInfo.Environment,
            SequenceOrder.InAnyOrder);
        foreach (var argument in startInfo.ArgumentList)
        {
            Assert.DoesNotContain(ProxyPassword, argument);
        }
    }

    [TestMethod]
    public void RunsTheCliInNodesPermissionModel()
    {
        var startInfo = Create(proxyUrl: null);

        Assert.AreSequenceEqual(ExpectedArguments, startInfo.ArgumentList);
        Assert.AreEqual("/app/scripts", startInfo.WorkingDirectory);
    }

    private static readonly string[] ExpectedArguments =
    [
        "--permission",
        "--allow-fs-read=/app/scripts/watch.ts",
        "--allow-fs-read=/app/node_modules",
        "--allow-fs-read=/app/src/watch-sdk",
        "--allow-fs-write=/tmp/out.json",
        "--allow-net",
        "--disable-warning=ExperimentalWarning",
        "--disable-warning=MODULE_TYPELESS_PACKAGE_JSON",
        "/app/src/watch-sdk/dist/cli.js",
        "run",
        "/app/scripts/watch.ts",
        "--output",
        "/tmp/out.json",
    ];
}
