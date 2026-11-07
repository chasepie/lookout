using Aspire.Hosting.Testing;
using ChasePie.Lookout.Core;
using YamlDotNet.Serialization;

namespace Chasepie.Lookout.AppHost.Tests;

/// <summary>
/// The Docker Compose files `aspire publish` writes: what each service can reach, how it's locked down, and which secrets
/// it gets. See docs/security.md.
/// </summary>
[TestClass]
public sealed class ProductionComposeTests
{
    private static Dictionary<string, object> _compose = null!;
    private static string _composeText = null!;
    private static string _envText = null!;

    // The services Lookout builds or configures to run read-only, as opposed to browserless and gluetun
    private static readonly string[] LookoutServices = ["backend", "runner", "gluetun-control", "gluetun-proxy"];

    private static readonly string[] RunnerNetworks = ["browsers", "runner"];
    private static readonly string[] OutwardServices = ["backend", "browserless", "gluetun"];
    private static readonly string[] BackendPorts = ["${LOOKOUT_BIND_ADDRESS:-127.0.0.1}:${LOOKOUT_PORT:-8080}:8080"];
    private static readonly string[] AllCapabilities = ["ALL"];

    [ClassInitialize]
    public static async Task PublishAsync(TestContext context)
    {
        var output = Directory.CreateTempSubdirectory("lookout-publish-").FullName;
        try
        {
            var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>(
                ["--operation", "publish", "--step", "publish", "--output-path", output], context.CancellationToken);
            await using var app = await builder.BuildAsync(context.CancellationToken);
            await app.RunAsync(context.CancellationToken);

            _composeText = await File.ReadAllTextAsync(Path.Join(output, "docker-compose.yaml"), context.CancellationToken);
            _envText = await File.ReadAllTextAsync(Path.Join(output, ".env"), context.CancellationToken);
            _compose = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(_composeText);
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [TestMethod]
    public void TheRunnerHasNoRouteOutButThroughBrowserless()
    {
        Assert.AreSequenceEqual(RunnerNetworks, Networks("runner").Order());
        foreach (var network in Networks("runner"))
        {
            Assert.IsTrue(IsInternal(network), $"{network} leads out");
        }
    }

    [TestMethod]
    [DataRow("browserless")]
    [DataRow("gluetun")]
    public void PagesScriptsVisitCanNotReachTheBackend(string service)
    {
        // Chromium loads them, and gluetun's proxy forwards to every network gluetun is on
        Assert.IsEmpty(Networks(service).Intersect(Networks("backend")));
    }

    [TestMethod]
    public void OnlyBrowserlessGluetunAndTheBackendLeadOut()
    {
        var outward = ServiceNames().Where(service => Networks(service).Any(network => !IsInternal(network)));

        Assert.AreSequenceEqual(OutwardServices, outward.Order());
    }

    [TestMethod]
    public void OnlyTheBackendIsPublishedAndOnLoopbackByDefault()
    {
        foreach (var service in ServiceNames().Where(service => service != "backend"))
        {
            Assert.IsFalse(Service(service).ContainsKey("ports"), $"{service} publishes a port");
        }

        Assert.AreSequenceEqual(BackendPorts, Strings(Service("backend"), "ports"));
    }

    [TestMethod]
    public void LookoutsServicesRunReadOnlyWithoutCapabilities()
    {
        foreach (var name in LookoutServices)
        {
            var service = Service(name);
            Assert.AreEqual("true", service.GetValueOrDefault("read_only")?.ToString(), $"{name} isn't read-only");
            Assert.AreSequenceEqual(AllCapabilities, Strings(service, "cap_drop"), name);
            Assert.Contains("no-new-privileges:true", Strings(service, "security_opt"), name);
        }
    }

    [TestMethod]
    [DataRow("backend")]
    [DataRow("runner")]
    public void TheDotNetServicesRunUnderAnInitSoACrashEndsThem(string service)
    {
        Assert.AreEqual("true", Service(service).GetValueOrDefault("init")?.ToString());
    }

    [TestMethod]
    public void TheRunnerHasAMemoryLimit()
    {
        var runner = Service("runner");

        Assert.AreEqual("${LOOKOUT_RUNNER_MEMORY:-1g}", Map(Map(Map(runner, "deploy"), "resources"), "limits")["memory"]);
    }

    [TestMethod]
    public void TheRunnerOnlyReadsTheScripts()
    {
        var scripts = Maps(Service("runner"), "volumes").Single();

        Assert.AreEqual("scripts", scripts["source"]);
        Assert.AreEqual("true", scripts["read_only"].ToString());
    }

    [TestMethod]
    [DataRow("LOOKOUT_OWNER_PASSWORD", "backend")]
    [DataRow("LOOKOUT_RUNNER_API_KEY", "backend,runner")]
    [DataRow("GLUETUN_PROXY_PASSWORD", "backend,gluetun,runner")]
    [DataRow("BROWSERLESS_TOKEN", "browserless,runner")]
    // gluetun gets it in its auth config
    [DataRow("GLUETUN_API_KEY", "backend")]
    [DataRow("OPENVPN_USER", "gluetun")]
    [DataRow("OPENVPN_PASSWORD", "gluetun")]
    public void EachSecretReachesOnlyTheServicesThatNeedIt(string secret, string services)
    {
        var reached = ServiceNames().Where(service =>
            Service(service).TryGetValue("environment", out var environment)
            && ((Dictionary<object, object>)environment).Values.Any(value => value.ToString()!.Contains($"${{{secret}}}")));

        Assert.AreSequenceEqual(services.Split(','), reached.Order());
    }

    [TestMethod]
    public void GluetunsKeyMayOnlyUseTheBackendsRoutes()
    {
        var auth = (string)Map(Map(_compose, "configs"), "gluetun_auth")["content"];

        Assert.Contains("""routes = ["GET /v1/vpn/settings", "PUT /v1/vpn/settings", "GET /v1/publicip/ip"]""", auth);
        Assert.Contains("apikey = \"${GLUETUN_API_KEY}\"", auth);
    }

    [TestMethod]
    public void NothingIsTakenFromThisMachine()
    {
        // No paths, and no values: every setting in .env is left for the deployment to fill in
        Assert.DoesNotContain(RepoRoot.FindRequired(AppContext.BaseDirectory), _composeText);
        foreach (var line in _envText.Split('\n').Where(line => line.Length > 0 && !line.StartsWith('#')))
        {
            Assert.EndsWith("=", line);
        }
    }

    [TestMethod]
    [DataRow("docker-compose.yaml")]
    [DataRow(".env.example")]
    public void TheCommittedDeploymentIsCurrent(string fileName)
    {
        var committed = File.ReadAllText(Path.Join(RepoRoot.FindRequired(AppContext.BaseDirectory), "deploy", fileName));

        Assert.AreEqual(
            fileName == ".env.example" ? _envText : _composeText,
            committed,
            $"deploy/{fileName} is out of date: run deploy/publish.sh and commit the result.");
    }

    private static IEnumerable<string> ServiceNames() => Map(_compose, "services").Keys.Select(key => (string)key);

    private static Dictionary<object, object> Service(string name) => Map(Map(_compose, "services"), name);

    private static List<string> Networks(string service) => Strings(Service(service), "networks");

    private static bool IsInternal(string network) =>
        Map(Map(_compose, "networks"), network).GetValueOrDefault("internal")?.ToString() == "true";

    private static Dictionary<object, object> Map(Dictionary<object, object> node, string key) =>
        (Dictionary<object, object>)node[key];

    private static Dictionary<object, object> Map(Dictionary<string, object> node, string key) =>
        (Dictionary<object, object>)node[key];

    private static List<string> Strings(Dictionary<object, object> node, string key) =>
        node.TryGetValue(key, out var value) ? ((List<object>)value).Select(item => item.ToString()!).ToList() : [];

    private static IEnumerable<Dictionary<object, object>> Maps(Dictionary<object, object> node, string key) =>
        ((List<object>)node[key]).Cast<Dictionary<object, object>>();
}
