using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ChasePie.Lookout.Backend.Runner;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class RunnerFailureTests(TestContext testContext)
{
    [TestMethod]
    [DataRow(503, "unavailable")]
    [DataRow(400, "{\"error\":\"timeoutMs must be an integer\"}")]
    [DataRow(200, "not json")]
    [DataRow(200, "{}")]
    [DataRow(200, "{\"outcome\":\"succeeded\",\"durationMs\":1}")]
    [DataRow(200, "{\"outcome\":\"unexpected\",\"durationMs\":1}")]
    [DataRow(200, "{\"outcome\":\"succeeded\",\"content\":\"bad\",\"durationMs\":-1}")]
    [DataRow(0, "Connection refused")]
    [DataRow(200, "{\"outcome\":\"succeeded\",\"content\":\"£20\",\"durationMs\":1}", "application/json; charset=bogus")]
    [DataRow(502, "<p>Bad gateway</p>", "text/html; charset=bogus")]
    public async Task RecordsAnErroredRunWhenTheRunnerCannotProvideAResult(int status, string body, string? contentType = null)
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient<IRunnerClient, HttpRunnerClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new RunnerResponseHandler(status, body, contentType))));
        using var client = host.CreateClient();
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Widget"), "name" },
            { new StringContent("https://example.com"), "notificationUrl" },
            { new StringContent("export default {}"), "script", "price.ts" }
        };
        var created = await client.PostAsync(new Uri("/api/watches", UriKind.Relative), form, testContext.CancellationToken);
        created.EnsureSuccessStatusCode();
        var watch = await created.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);

        var response = await client.PostAsync(new Uri($"/api/watches/{watch!["id"]}/runs", UriKind.Relative), null, testContext.CancellationToken);

        response.EnsureSuccessStatusCode();
        var run = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.AreEqual("errored", run!["outcome"]!.GetValue<string>());
        Assert.IsFalse(run["isChange"]!.GetValue<bool>());
        Assert.IsFalse(string.IsNullOrWhiteSpace(run["errorMessage"]!.GetValue<string>()));
        if (status == 400)
        {
            Assert.Contains("timeoutMs must be an integer", run["errorMessage"]!.GetValue<string>());
        }
        var watches = await client.GetFromJsonAsync<JsonArray>("/api/watches", testContext.CancellationToken);
        Assert.IsNull(watches![0]!["baseline"]);
        Assert.AreEqual("errored", watches[0]!["lastRun"]!["outcome"]!.GetValue<string>());
    }

    private sealed class RunnerResponseHandler(int status, string body, string? contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (status == 0)
            {
                throw new HttpRequestException(body);
            }
            var content = new StringContent(body);
            if (contentType is not null)
            {
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content });
        }
    }
}
