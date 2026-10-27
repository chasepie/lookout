using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChasePie.Lookout.Backend.IntegrationTests;

/// <summary>The Account set from <c>Account:UserName</c> and <c>Account:Password</c> on every start (#47).</summary>
[TestClass]
public sealed class ConfiguredAccountTests(TestContext testContext)
{
    [TestMethod]
    public async Task CreatesTheAccountFromTheSettingsBeforeTheFirstRequest()
    {
        await using var factory = new LookoutWebApplicationFactory();
        await using var host = WithAccountSettings(factory, "owner", "correct horse");
        using var client = await host.CreateAppClientAsync(testContext.CancellationToken);

        Assert.IsTrue((await SessionStatus(client))["accountExists"]!.GetValue<bool>());
        var setup = await client.PostAsJsonAsync("/api/setup", new { userName = "intruder", password = "another password" }, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Conflict, setup.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await Login(client, "owner", "correct horse")).StatusCode);
    }

    [TestMethod]
    [DataRow("someone", "correct horse")]
    [DataRow("owner", "battery staple")]
    [DataRow("Owner", "correct horse")]
    [DataRow("someone", "battery staple")]
    public async Task MakesAnExistingAccountMatchTheSettingsAndEndsItsSessions(string userName, string password)
    {
        await using var factory = new LookoutWebApplicationFactory();
        var databasePath = NewDatabasePath();
        try
        {
            string cookie;
            await using (var before = WithAccountSettings(factory, null, null, databasePath))
            {
                cookie = await SetUpAsync(before);
            }

            await using var after = WithAccountSettings(factory, userName, password, databasePath);
            using var client = await after.CreateAppClientAsync(testContext.CancellationToken);

            Assert.AreEqual(HttpStatusCode.Unauthorized, await WatchesStatus(after, cookie));
            // Login ignores the user name's case.
            if (!string.Equals(userName, SignedInClients.UserName, StringComparison.OrdinalIgnoreCase)
                || password != SignedInClients.Password)
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await Login(client, SignedInClients.UserName, SignedInClients.Password)).StatusCode);
            }
            Assert.AreEqual(HttpStatusCode.NoContent, (await Login(client, userName, password)).StatusCode);
            Assert.AreEqual(userName, (await SessionStatus(client))["userName"]!.GetValue<string>());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow(" owner ")]
    public async Task KeepsSessionsWhenStartedAgainWithTheSameSettings(string userName)
    {
        await using var factory = new LookoutWebApplicationFactory();
        var databasePath = NewDatabasePath();
        try
        {
            string cookie;
            await using (var before = WithAccountSettings(factory, null, null, databasePath))
            {
                cookie = await SetUpAsync(before);
            }

            await using (var first = WithAccountSettings(factory, userName, SignedInClients.Password, databasePath))
            {
                Assert.AreEqual(HttpStatusCode.OK, await WatchesStatus(first, cookie));
            }
            await using var again = WithAccountSettings(factory, userName, SignedInClients.Password, databasePath);

            Assert.AreEqual(HttpStatusCode.OK, await WatchesStatus(again, cookie));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public async Task LeavesSetupAndTheAccountToTheAppWithoutTheSettings(string? unset)
    {
        await using var factory = new LookoutWebApplicationFactory();
        var databasePath = NewDatabasePath();
        try
        {
            string cookie;
            await using (var before = WithAccountSettings(factory, unset, unset, databasePath))
            {
                using var client = await before.CreateAppClientAsync(testContext.CancellationToken);
                Assert.IsFalse((await SessionStatus(client))["accountExists"]!.GetValue<bool>());
                cookie = await SetUpAsync(before);
            }

            await using var after = WithAccountSettings(factory, unset, unset, databasePath);

            Assert.AreEqual(HttpStatusCode.OK, await WatchesStatus(after, cookie));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    public static IEnumerable<(string? UserName, string? Password, string Setting)> RefusedSettings =>
    [
        ("someone", null, "Account:Password"),
        ("someone", "", "Account:Password"),
        (null, "battery staple", "Account:UserName"),
        ("   ", "battery staple", "Account:UserName"),
        (new string('a', 101), "battery staple", "Account:UserName"),
        ("someone", "seven c", "Account:Password"),
        ("someone", new string('p', 1025), "Account:Password"),
    ];

    [TestMethod]
    [DynamicData(nameof(RefusedSettings))]
    public async Task RefusesToStartWithOnlyOneSettingOrAValueSetupWouldRefuse(string? userName, string? password, string setting)
    {
        await using var factory = new LookoutWebApplicationFactory();
        var databasePath = NewDatabasePath();
        try
        {
            string cookie;
            await using (var before = WithAccountSettings(factory, null, null, databasePath))
            {
                cookie = await SetUpAsync(before);
            }

            await using (var refused = WithAccountSettings(factory, userName, password, databasePath))
            {
                var error = Assert.ThrowsExactly<InvalidOperationException>(() => refused.CreateClient());

                Assert.Contains(setting, error.Message);
                if (!string.IsNullOrEmpty(password))
                {
                    Assert.DoesNotContain(password, error.Message);
                }
            }

            await using var after = WithAccountSettings(factory, null, null, databasePath);
            Assert.AreEqual(HttpStatusCode.OK, await WatchesStatus(after, cookie));
            // Logging in with the credentials setup gave shows they're unchanged.
            using var loggedIn = await after.CreateLoggedInClientAsync(testContext.CancellationToken);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [TestMethod]
    public async Task CreatesNoAccountWhenItRefusesToStart()
    {
        await using var factory = new LookoutWebApplicationFactory();
        var databasePath = NewDatabasePath();
        try
        {
            await using (var refused = WithAccountSettings(factory, "someone", "seven c", databasePath))
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => refused.CreateClient());
            }

            await using var after = WithAccountSettings(factory, null, null, databasePath);
            using var client = await after.CreateAppClientAsync(testContext.CancellationToken);

            Assert.IsFalse((await SessionStatus(client))["accountExists"]!.GetValue<bool>());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>Sets up the host's Account in the app, as without the settings, and returns its session cookie.</summary>
    private async Task<string> SetUpAsync(WebApplicationFactory<Program> host)
    {
        using var client = await host.CreateAppClientAsync(testContext.CancellationToken);
        var setup = await client.PostAsJsonAsync("/api/setup", new { userName = SignedInClients.UserName, password = SignedInClients.Password }, testContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, setup.StatusCode);
        return setup.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("Lookout.Session=", StringComparison.Ordinal))
            .Split(';')[0];
    }

    /// <summary>What the host answers a request for the Watches that carries <paramref name="cookie"/>.</summary>
    private async Task<HttpStatusCode> WatchesStatus(WebApplicationFactory<Program> host, string cookie)
    {
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/watches");
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request, testContext.CancellationToken);
        return response.StatusCode;
    }

    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"lookout-test-{Guid.NewGuid():N}.db");

    private static WebApplicationFactory<Program> WithAccountSettings(
        WebApplicationFactory<Program> factory, string? userName, string? password, string? databasePath = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            if (databasePath is not null)
            {
                builder.UseSetting("ConnectionStrings:Lookout", $"Data Source={databasePath};Pooling=False");
            }
            builder.UseSetting("Account:UserName", userName);
            builder.UseSetting("Account:Password", password);
        });

    private Task<HttpResponseMessage> Login(HttpClient client, string userName, string password) =>
        client.PostAsJsonAsync("/api/session", new { userName, password }, testContext.CancellationToken);

    private async Task<JsonObject> SessionStatus(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonObject>("/api/session", testContext.CancellationToken))!;
}
