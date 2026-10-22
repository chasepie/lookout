using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ChasePie.Lookout.Backend.IntegrationTests;

[TestClass]
public sealed class SettingsTests(TestContext testContext)
{
    [TestMethod]
    public async Task KeepsRunsForThirtyDaysByDefault()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var settings = await client.GetFromJsonAsync<JsonObject>("/api/settings", testContext.CancellationToken);

        Assert.AreEqual(30, settings!["retentionDays"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task ChangesTheRetentionPeriod()
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var response = await client.PutAsJsonAsync("/api/settings", new { retentionDays = 7 }, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<JsonObject>(testContext.CancellationToken);
        Assert.AreEqual(7, saved!["retentionDays"]!.GetValue<int>());
        var settings = await client.GetFromJsonAsync<JsonObject>("/api/settings", testContext.CancellationToken);
        Assert.AreEqual(7, settings!["retentionDays"]!.GetValue<int>());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(3651)]
    public async Task RejectsARetentionPeriodOutsideOneDayToTenYears(int retentionDays)
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateSignedInClientAsync(testContext.CancellationToken);

        var response = await client.PutAsJsonAsync("/api/settings", new { retentionDays }, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var settings = await client.GetFromJsonAsync<JsonObject>("/api/settings", testContext.CancellationToken);
        Assert.AreEqual(30, settings!["retentionDays"]!.GetValue<int>());
    }

    [TestMethod]
    [DataRow("GET")]
    [DataRow("PUT")]
    public async Task RequiresALogin(string method)
    {
        await using var factory = new LookoutWebApplicationFactory();
        using var client = await factory.CreateAppClientAsync(testContext.CancellationToken);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/settings")
        {
            Content = method == "PUT" ? JsonContent.Create(new { retentionDays = 7 }) : null
        };

        var response = await client.SendAsync(request, testContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
