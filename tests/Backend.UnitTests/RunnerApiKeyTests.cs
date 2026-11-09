using System.Net;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.UnitTests;

[TestClass]
public sealed class RunnerApiKeyTests
{
    private static readonly string Key = new('k', RunnerApiKey.MinLength);

    public TestContext TestContext { get; set; } = null!;

    private static IConfiguration Configuration(string? apiKey) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EnvVars.LookoutRunnerUrl] = "https://runner.test",
                [EnvVars.LookoutRunnerApiKey] = apiKey,
            })
            .Build();

    [TestMethod]
    public void ReadsTheKeyFromConfiguration()
    {
        var apiKey = RunnerApiKey.FromConfiguration(Configuration(Key));

        Assert.AreEqual(Key, apiKey.Value);
    }

    // Missing, too short, or not carried intact by an HTTP header
    public static IEnumerable<object?[]> InvalidKeys =>
    [
        [null],
        [""],
        [new string(' ', RunnerApiKey.MinLength)],
        [new string('k', RunnerApiKey.MinLength - 1)],
        [Key + " "],
        [" " + Key],
        [Key + "é"],
        [Key + "\t"],
        [Key + "\n"],
    ];

    [TestMethod]
    public void AcceptsSpacesInsideTheKey()
    {
        var value = Key[..16] + " " + Key[16..];

        Assert.AreEqual(value, new RunnerApiKey(value).Value);
    }

    [TestMethod]
    [DynamicData(nameof(InvalidKeys))]
    public void RefusesAnInvalidKey(string? value)
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => RunnerApiKey.FromConfiguration(Configuration(value)));

        Assert.Contains(EnvVars.LookoutRunnerApiKey, ex.Message);
    }

    [TestMethod]
    public void MatchesOnlyTheKey()
    {
        var apiKey = new RunnerApiKey(Key);

        Assert.IsTrue(apiKey.Matches(Key));
        Assert.IsFalse(apiKey.Matches(null));
        Assert.IsFalse(apiKey.Matches(""));
        Assert.IsFalse(apiKey.Matches(Key.ToUpperInvariant()));
        Assert.IsFalse(apiKey.Matches(Key + "k"));
    }

    [TestMethod]
    public async Task TheBackendSendsTheKeyToTheRunner()
    {
        var configuration = Configuration(Key);
        var handler = new RecordingHandler(HttpStatusCode.OK);
        await using var provider = CreateServices(configuration, handler);

        await provider.GetRequiredService<RunnerHttpClient>()
            .RunWatchScriptAsync("script.ts", useVpn: false, TestContext.CancellationToken);

        Assert.IsNotNull(handler.Request);
        Assert.AreEqual(Key, handler.Request.Headers.GetValues(RunnerApiKey.HeaderName).Single());
    }

    [TestMethod]
    public async Task ARefusedKeyNamesTheVariableToCheck()
    {
        await using var provider = CreateServices(Configuration(Key), new RecordingHandler(HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => provider.GetRequiredService<RunnerHttpClient>()
            .RunWatchScriptAsync("script.ts", useVpn: false, TestContext.CancellationToken));

        Assert.AreEqual(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains(EnvVars.LookoutRunnerApiKey, ex.Message);
    }

    // The Backend's registration of RunnerHttpClient, with the Runner replaced by the handler
    private static ServiceProvider CreateServices(IConfiguration configuration, HttpMessageHandler handler)
    {
        var services = new ServiceCollection()
            .AddSingleton(configuration)
            .AddSingleton(RunnerApiKey.FromConfiguration(configuration));
        services.AddHttpClient<RunnerHttpClient>(RunnerHttpClient.Configure)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                // A refused key gets an empty body, like the Runner's challenge
                Content = statusCode == HttpStatusCode.OK
                    ? new StringContent("""{"success":true,"content":""}""", null, "application/json")
                    : new StringContent(""),
            });
        }
    }
}
