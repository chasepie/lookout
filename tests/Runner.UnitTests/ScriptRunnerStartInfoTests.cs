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

    // What the Runner's own environment holds when the AppHost starts it, plus a CLI proxy left over from a shell
    private static readonly Dictionary<string, string> RunnerEnvironment = new()
    {
        [LookoutEnvironmentVariables.VpnProxyUrl] = ProxyUrl,
        [LookoutEnvironmentVariables.RunnerApiKey] = new string('k', RunnerApiKey.MinLength),
        [ScriptRunner.CliProxyUrlVariable] = ProxyUrl,
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
        ScriptRunner.CreateStartInfo("/repo/cli.js", "/scripts/watch.ts", "/tmp/out.json", proxyUrl);

    [TestMethod]
    public void AVpnRunGetsTheProxyInItsEnvironmentAndNotOnItsCommandLine()
    {
        var startInfo = Create(ProxyUrl);

        Assert.AreEqual(ProxyUrl, startInfo.Environment[ScriptRunner.CliProxyUrlVariable]);
        Assert.IsFalse(startInfo.Environment.ContainsKey(LookoutEnvironmentVariables.VpnProxyUrl));
        foreach (var argument in startInfo.ArgumentList)
        {
            Assert.DoesNotContain(ProxyPassword, argument);
        }
    }

    [TestMethod]
    public void ARunWithoutTheVpnDoesNotInheritTheProxy()
    {
        var startInfo = Create(proxyUrl: null);

        Assert.IsFalse(startInfo.Environment.ContainsKey(ScriptRunner.CliProxyUrlVariable));
        Assert.IsFalse(startInfo.Environment.ContainsKey(LookoutEnvironmentVariables.VpnProxyUrl));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void NoRunGetsTheRunnerApiKey(bool useVpn)
    {
        var startInfo = Create(useVpn ? ProxyUrl : null);

        Assert.IsFalse(startInfo.Environment.ContainsKey(LookoutEnvironmentVariables.RunnerApiKey));
    }
}
