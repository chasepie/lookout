using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Net.Http.Headers;

namespace ChasePie.Lookout.Backend.IntegrationTests;

public static class SignedInClients
{
    public const string UserName = "owner";
    public const string Password = "correct horse";
    public const string RequestTokenHeader = RequestForgery.TokenHeaderName;

    /// <summary>Creates a client that has set up the host's account and holds its session cookie.</summary>
    public static async Task<HttpClient> CreateSignedInClientAsync(this WebApplicationFactory<Program> factory,
        CancellationToken cancellationToken)
    {
        var client = await factory.CreateAppClientAsync(cancellationToken);
        try
        {
            var setup = await client.PostAsJsonAsync("/api/setup", new { userName = UserName, password = Password }, cancellationToken);
            setup.EnsureSuccessStatusCode();
            await client.RefreshRequestTokenAsync(cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Creates a client logged in to the account an earlier host set up against the same database.</summary>
    public static async Task<HttpClient> CreateLoggedInClientAsync(this WebApplicationFactory<Program> factory,
        CancellationToken cancellationToken)
    {
        var client = await factory.CreateAppClientAsync(cancellationToken);
        try
        {
            var login = await client.PostAsJsonAsync("/api/session", new { userName = UserName, password = Password }, cancellationToken);
            login.EnsureSuccessStatusCode();
            await client.RefreshRequestTokenAsync(cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Creates a client without a session that, like the Angular app, sends back Lookout's request token.</summary>
    public static async Task<HttpClient> CreateAppClientAsync(this WebApplicationFactory<Program> factory,
        CancellationToken cancellationToken)
    {
        var client = factory.CreateClient();
        try
        {
            await client.RefreshRequestTokenAsync(cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Fetches the request token for the client's current session, as the app does after every login or logout.</summary>
    public static async Task RefreshRequestTokenAsync(this HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri("/api/session", UriKind.Relative), cancellationToken);
        response.EnsureSuccessStatusCode();
        var token = SetCookieHeaderValue.ParseList(response.Headers.GetValues("Set-Cookie").ToList())
            .Single(cookie => cookie.Name == RequestForgery.TokenCookieName).Value.ToString();
        client.DefaultRequestHeaders.Remove(RequestTokenHeader);
        client.DefaultRequestHeaders.Add(RequestTokenHeader, token);
    }
}
