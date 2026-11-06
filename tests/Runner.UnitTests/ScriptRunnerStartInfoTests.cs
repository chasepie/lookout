using System.Diagnostics;
using ChasePie.Lookout.Core;
using ChasePie.Lookout.Runner.Services;

namespace ChasePie.Lookout.Runner.UnitTests;

// The tests set the Runner's environment variables, which are shared by the whole process
[TestClass]
[DoNotParallelize]
public sealed class ScriptRunnerStartInfoTests
{
    private const string ProxyUrl = "http://lookout:secret@gluetun:8888";

    // What the Runner's own environment holds when the AppHost starts it
    [ClassInitialize]
    public static void SetRunnerEnvironment(TestContext _)
    {
        Environment.SetEnvironmentVariable(LookoutEnvironmentVariables.VpnProxyUrl, ProxyUrl);
        Environment.SetEnvironmentVariable(LookoutEnvironmentVariables.RunnerApiKey, new string('k', RunnerApiKey.MinLength));
    }

    [ClassCleanup]
    public static void ResetRunnerEnvironment()
    {
        Environment.SetEnvironmentVariable(LookoutEnvironmentVariables.VpnProxyUrl, null);
        Environment.SetEnvironmentVariable(LookoutEnvironmentVariables.RunnerApiKey, null);
    }

    private static ProcessStartInfo Create(string? proxyUrl) =>
        ScriptRunner.CreateStartInfo("/repo/cli.js", "/scripts/watch.ts", "/tmp/out.json", proxyUrl);

    [TestMethod]
    public void AVpnRunGetsTheProxyInItsEnvironmentAndNotOnItsCommandLine()
    {
        var startInfo = Create(ProxyUrl);

        Assert.AreEqual(ProxyUrl, startInfo.Environment[LookoutEnvironmentVariables.VpnProxyUrl]);
        Assert.DoesNotContain(ProxyUrl, startInfo.ArgumentList);
        Assert.DoesNotContain("--proxy", startInfo.ArgumentList);
    }

    [TestMethod]
    public void ARunWithoutTheVpnDoesNotInheritTheProxy()
    {
        var startInfo = Create(proxyUrl: null);

        Assert.IsFalse(startInfo.Environment.ContainsKey(LookoutEnvironmentVariables.VpnProxyUrl));
        Assert.DoesNotContain("--proxy", startInfo.ArgumentList);
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
