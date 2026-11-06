using System.Net;
using ChasePie.Lookout.Backend.Services;
using ChasePie.Lookout.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.UnitTests;

[TestClass]
public sealed class RunnerApiKeyTests
{
    private static readonly string Key = new('k', RunnerApiKey.MinLength);

    public TestContext TestContext { get; set; } = null!;

    private static IConfiguration Configuration(string? apiKey) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [LookoutEnvironmentVariables.RunnerUrl] = "https://runner.test",
                [LookoutEnvironmentVariables.RunnerApiKey] = apiKey,
            })
            .Build();

    [TestMethod]
    public void ReadsTheKeyFromConfiguration()
    {
        var apiKey = RunnerApiKey.FromConfiguration(Configuration(Key));

        Assert.AreEqual(Key, apiKey.Value);
    }

    public static IEnumerable<object?[]> MissingOrShortKeys =>
    [
        [null],
        [""],
        [new string(' ', RunnerApiKey.MinLength)],
        [new string('k', RunnerApiKey.MinLength - 1)],
    ];

    [TestMethod]
    [DynamicData(nameof(MissingOrShortKeys))]
    public void RefusesAMissingOrShortKey(string? value)
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => RunnerApiKey.FromConfiguration(Configuration(value)));

        Assert.Contains(LookoutEnvironmentVariables.RunnerApiKey, ex.Message);
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
        var handler = new RecordingHandler();
        var services = new ServiceCollection()
            .AddSingleton(configuration)
            .AddSingleton(RunnerApiKey.FromConfiguration(configuration));
        services.AddHttpClient<RunnerHttpClient>(RunnerHttpClient.Configure)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<RunnerHttpClient>()
            .RunWatchScriptAsync("script.ts", useVpn: false, TestContext.CancellationToken);

        Assert.IsNotNull(handler.Request);
        Assert.AreEqual(Key, handler.Request.Headers.GetValues(RunnerApiKey.HeaderName).Single());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"success":true,"content":""}""", null, "application/json"),
            });
        }
    }
}
